# Project improvement review — 8 September 2026

The highest-value work is billing correctness and recovery, followed by operator workflow and release verification. The existing Desktop/API split, synchronous Tally integration, append-only bill audit, shared contracts, and extracted bill workflows are sound foundations. Keep those boundaries and improve them in small slices.

**Scope and evidence.** Reviewed the architecture and selected workflows across seven production projects and two test projects, with repository inventory, source tracing, documentation comparison, dependency checking, and executable probes. The checkout contained 484 tracked files at commit `48797cb`, plus existing uncommitted Desktop, ServerTray, and documentation work. Those existing edits were preserved. This is a source and test review; no native UI walkthrough, real printer job, real Tally write, production database access, or deployment was performed.

The existing graph was used for navigation. It dates to 14 June, references commit `aad6e458`, and reports an old node-ID scheme with same-name collisions. Its connectivity counts are not treated as current architectural measurements.

**Verification results**

| Check | Result |
|---|---|
| Release solution build, `--no-restore -warnaserror` | Passed: zero warnings and errors |
| Release solution tests, `--no-build` | 358 passed, 0 failed, 9 Postgres tests skipped |
| NuGet audit, including transitive dependencies | No known vulnerable packages reported by the configured NuGet source |
| Formatting verification | Failed: import ordering in two test files; see finding 14 |
| Six isolated billing probes | Reproduced the behaviors described in findings 1–6 |
| Relational verification | Not run: the local Docker named pipe was unavailable |

The audit harness uses real billing/numbering services, fake Tally, and fresh EF InMemory databases. Its edit-during-preflight probe deliberately interleaves two contexts; it is evidence of a stale-revision logic defect, not proof of PostgreSQL locking behavior. Likewise, its suffix probe exposes a duplicated candidate; PostgreSQL's unique index should reject that candidate instead of storing a duplicate.

Local reproduction: [AuditProbes.csproj](D:/Projects/Tally_Wrapper_V2/artifacts/audit-20260908/AuditProbes.csproj), [probe source](D:/Projects/Tally_Wrapper_V2/artifacts/audit-20260908/Program.cs), [results](D:/Projects/Tally_Wrapper_V2/artifacts/audit-20260908/probe-results.txt). Build/test/package logs are in the same ignored artifact directory. These artifacts remain local unless deliberately included in a later change.

```powershell
dotnet run --project artifacts/audit-20260908/AuditProbes.csproj --configuration Release
```

**Priority register**

P1 means an important correctness or recovery issue under the stated trigger. P2 means a reliability, workflow, or engineering improvement. Effort estimates assume focused changes with regression checks; they are planning estimates, not commitments.

| ID | Priority | Improvement | Approximate effort |
|---|---|---|---|
| 1 | P1 | Make bill transitions and the posted revision agree under concurrent operations | 3–5 days |
| 2 | P1 | Preserve create semantics when editing a rejected first push | 0.5–1 day |
| 3 | P1 | Reserve a number when creating a revised bill | 1–2 days |
| 4 | P1 | Persist the posting outcome after request cancellation | 1–2 days |
| 5 | P1 | Make Save Bill idempotent across lost responses | 2–4 days |
| 6 | P1 | Make sequence occupancy and rollback work with suffixes | 2–4 days |
| 7 | P1, conditional | Scope startup recovery to abandoned posting attempts | 2–4 days |
| 8 | P2 | Complete the draft-lease editing workflow | 2–4 days |
| 9 | P2 | Version shared settings and pin posting configuration | 2–3 days |
| 10 | P2 | Make partial synthetic batches identifiable and resumable | 2–4 days |
| 11 | P2 | Align timeouts and honor database connection settings | 1–2 days |
| 12 | P2 | Protect unsaved entry and finish existing functional UI fixes | Separate small slices |
| 13 | P2 | Bound print preparation and refresh printer discovery | 1–3 days |
| 14 | P2 | Repair the current formatting gate and F5 paths | A few hours |
| 15 | P2 | Strengthen scenario tests, release checks, and diagnostics | Incremental |
| 16 | P2/P3 | Consolidate verified duplication and correct stale guidance | 1–2 days |

**1. Post the revision that was actually claimed, and protect all bill transitions**

Evidence: [BillPostingWorkflow.cs:132](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillPostingWorkflow.cs:132) loads the current revision before awaiting Tally preflight. Its conditional update checks eligible state but not the revision/version. Reloading the bill at line 216 does not reload the previously captured revision. [BillLifecycleWorkflow.cs:32](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillLifecycleWorkflow.cs:32) checks state and later saves an edit without a concurrency token. The model has no bill/revision concurrency token.

**Demonstrated:** load a ₹100 bill for push, save a ₹200 revision in another context during preflight, then resume push. The fake poster receives ₹100 while the persisted current revision is ₹200 and bill state is `posted`.

Use a bill version plus expected current revision in the posting claim. Load/send the exact revision covered by that claim. Apply equivalent conditional protection to edit, void, revise, renumber, delete, and admin resolution so a stale reader cannot override an intervening operation. Return a useful 409 with current state/version. An EF concurrency token also needs explicit handling in `ExecuteUpdate`; it is not automatically applied there. See [Microsoft's concurrency guidance](https://learn.microsoft.com/en-us/ef/core/saving/concurrency) and [ExecuteUpdate concurrency handling](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete).

Acceptance: deterministic Postgres tests for push-vs-edit, edit-vs-edit, push-vs-void, push-vs-renumber, and push-vs-delete. Preserve the committed `posting` transition before Tally; do not hold a database transaction across the Tally call.

**2. Editing a failed first push incorrectly selects Alter**

Evidence: [BillLifecycleWorkflow.cs:75](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillLifecycleWorkflow.cs:75) sets `EditedAfterPush = true` when reopening a failed bill, regardless of whether it ever posted successfully. [BillPostingWorkflow.cs:222](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillPostingWorkflow.cs:222) uses that flag to choose Alter.

**Demonstrated:** first push receives a definite rejection, operator corrects the bill, then pushes again. It fails with `TALLY_ALTER_TARGET_MISSING`; the second attempt never reaches the poster because no original voucher exists.

Derive Alter from evidence of an existing voucher, preserving the flag for failed alterations. Prefer an explicit stored remote voucher identity as this evolves; an arbitrary failed state is insufficient. Acceptance: reject-first-create → edit → successful Create, alongside posted → edit → Alter and failed Alter → edit → Alter tests. Keep the uncertain-outcome reconciliation restriction.

**3. Revised bills cannot be pushed because they receive no reservation**

Evidence: [BillLifecycleWorkflow.cs:128](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillLifecycleWorkflow.cs:128) creates a new pending bill without `InvoiceNumber` or `FiscalYear`. Normal creation reserves them. Push explicitly rejects missing numbering. The existing revise test checks creation and supersession, but does not continue to push.

**Demonstrated:** revise → new bill has both fields null → push throws `BillStateConflictException`.

Reserve the revised bill's number and persist the reservation, new bill/revision, old-bill supersession, and audit atomically. Reuse the existing reservation path. Acceptance: revise → retrieve → print content → push, with a Postgres rollback check proving failure leaves the original bill and sequence consistent.

**4. Client cancellation can strand a bill in posting until an API restart**

Evidence: [BillPostingWorkflow.cs:281](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillPostingWorkflow.cs:281) converts an exception after invoking the poster to Unknown, but the final save at line 378 uses the same request cancellation token. [StuckPostingRecoveryHostedService.cs:48](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Tally/StuckPostingRecoveryHostedService.cs:48) only runs at startup.

**Demonstrated:** cancel after the poster is entered. The call throws `TaskCanceledException`; a fresh context sees `posting` and no terminal outcome audit.

Once the posting claim is committed, use a separate, bounded finalization token to record the known or uncertain outcome even if the HTTP caller disconnects. Stop further batch work when canceled, but settle the in-flight attempt. Retain startup recovery if the database itself cannot accept the final write. Do not retry the voucher automatically. Test cancellation before send, during send, after acceptance, and during final persistence, plus a real HTTP disconnect contract test.

**5. The reservation key does not make Save Bill replay-safe**

Evidence: [CreateBillDraftRequest](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Contracts/Bills/BillContracts.cs:70) carries no client operation identifier. [BillLifecycleWorkflow.cs:196](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Bills/BillLifecycleWorkflow.cs:196) creates a new bill ID on every request and derives `draft:{billId}` from it. [InvoiceViewModel.cs:554](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/Invoice/InvoiceViewModel.cs:554) only remembers the bill ID after receiving the response.

**Demonstrated:** replaying the same create request produces distinct bills and numbers `0001`/`0002`. This is operationally problematic when the first commit succeeds but its response is lost; the operator still sees an unsaved form and retries. It is not a proposal to deduplicate intentional bills by matching their contents.

Create and retain a client save-operation ID before sending. Enforce uniqueness server-side, return the existing result on a replay with the same payload, and reject conflicting reuse. Apply a corresponding strategy to retries of draft updates. Test a dropped response after commit and a restart while the save outcome is unknown. Correct the current promise in [docs/04:47](D:/Projects/Tally_Wrapper_V2/docs/04_numbering_and_idempotency.md:47) that a retried save already returns the same number.

**6. Suffixes break sequence occupancy and trailing rollback**

Evidence: [InvoiceNumberFormatter.cs:12](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Contracts/Numbering/InvoiceNumberFormatter.cs:12) appends arbitrary suffixes. Its parser at line 35 only reads digits at the very end. [CloudSettingsValidator.cs:26](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Application/Settings/CloudSettingsValidator.cs:26) permits suffix text. Both allocation and rollback depend on that parser.

**Demonstrated:** with suffix `-A`, create bills 1 and 2, delete bill 2, then reserve again. The allocator selects the existing `DEV-2026-27/0001-A`. A suffix `-A2` on core 99 parses as core 2. The InMemory probe permits the duplicate; PostgreSQL should reject the save on its unique index.

Use an explicit numeric core associated with the bill/reservation, independent of its display text. Backfill using reservation evidence and the historical formatting rules; ambiguous legacy numbers need explicit handling. Do not silently clear existing suffixes. Cover letter suffixes, digit suffixes, suffixes containing the fiscal year, prefix changes, mixed padding, rename, and delete on Postgres.

This also offers a focused performance improvement: [NumberingService.cs:297](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Numbering/NumberingService.cs:297) loads every invoice number in the fiscal-year scope while holding the sequence lock. The reclaimer does another full scope scan. A numeric core with suitable indexing can avoid transferring and parsing an ever-growing year of bills on every save. Benchmark 10k/100k scopes before selecting the query/index strategy.

**7. Recovery needs an abandoned-attempt boundary**

Evidence: [StuckPostingRecoveryHostedService.cs:67](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Tally/StuckPostingRecoveryHostedService.cs:67) selects every `posting` bill and writes reconciliation state. It does not filter by attempt owner, startup cutoff, or whether another API instance is still performing that write.

This is a source-supported risk when multiple embedded/server API instances share a database, or a deferred recovery scan overlaps newly accepted work. It was not reproduced against Postgres in this pass. Starting another API must not classify a live operation as abandoned or overwrite its newly settled state.

Either enforce one writer API per database explicitly, or track posting-attempt ownership/liveness and use a conditional recovery transition. A startup timestamp alone does not establish that another process is dead. Test API A paused in posting while API B starts, plus a scan whose read and update straddle finalization. Recovery must remain local state repair and never send Tally vouchers.

**8. The lease feature is exposed but not integrated with editing**

Evidence: [DraftLeaseApiClient.cs](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/Services/DraftLeaseApiClient.cs) implements acquire/renew/release, but production ViewModel references only use list/force-release in the admin screen. [InvoiceViewModel.cs:684](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/Invoice/InvoiceViewModel.cs:684) loads a bill without a lease; the update DTO contains no lease or expected revision.

Wire acquire → renew → save/release into editing, surface who owns a lock, and enforce ownership/version on the server. A lock display alone cannot protect against stale saves. [DraftLeaseService.cs:27](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Leases/DraftLeaseService.cs:27) also uses read-then-insert; simultaneous acquisitions can reach the partial unique index and produce an unmapped database error instead of the typed 409. Cover acquisition, expiry/reacquisition, renewal/release races, and a competing edit in Postgres. Coordinate this with finding 1 rather than building two competing locking mechanisms.

**9. Shared settings need conflict protection and a consistent posting snapshot**

Evidence: [CloudSettingsService.cs:42](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Settings/CloudSettingsService.cs:42) overwrites the complete settings row without an expected version. Two counters can read the same settings, change different fields, and the later whole-object save can revert the first operator's changes. Settings contains the active Tally company and ledger mappings, so this matters beyond cosmetic preferences.

Use a versioned settings response/update and prompt for reload or reconciliation on conflict. Keep full-form save if it suits the UI. Also pass one immutable connection/company/ledger snapshot through preflight, XML building, and transport: [TallyPoster.cs:19](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Tally/TallyPoster.cs:19) and [TallyXmlClient.cs:51](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Tally/TallyXmlClient.cs:51) currently obtain settings separately. The source permits settings changes between stages; no misrouted live voucher was demonstrated. Test an active-company/endpoint change during a paused operation.

**10. A partial synthetic batch has no durable run identity**

Evidence: [SyntheticBatchExecutor.cs:53](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Application/Bills/SyntheticBatchExecutor.cs:53) builds a fresh random plan and commits one bill at a time. Created IDs are held in a local list and returned only after the loop completes. If bill N fails or the response is lost, earlier commits remain and retry builds a new plan.

Preserve a run ID, planned entries, per-entry idempotency keys, and progress, with a read endpoint for the actual outcome. Resumption should be operator-initiated and synchronous. Keep the existing per-bill transaction granularity and the frozen amount limits. Acceptance: fail midway, inspect exactly which bills exist, resume explicitly, and verify no duplicate entries. Do not turn this into a Tally background queue.

**11. Timeouts and pool settings should describe actual runtime behavior**

Evidence: [TallyXmlClient.cs:60](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/Tally/TallyXmlClient.cs:60) allows a 300-second operation budget, while the Tally HttpClient registration and Desktop bills/master clients leave `HttpClient.Timeout` unset. Its documented default is 100 seconds. A sequential batch can also exceed the outer client timeout although individual Tally calls are healthy. See [Microsoft's Timeout reference](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.timeout?view=net-10.0).

Define separate connect, operation, batch, and final-persistence budgets. Configure HttpClient consistently with the operation policy, and expose batch progress/cancellation without replaying writes. Test a slow response and an aggregate batch that exceeds 100 seconds using controlled delays.

Separately, [DependencyInjection.cs:44](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Infrastructure/DependencyInjection.cs:44) unconditionally changes `MinPoolSize` to 2 and `KeepAlive` to 30, overriding explicit runtime configuration. Honor supplied values and apply documented defaults only when absent. This matters to the repo's OpenBao/PgBouncer configuration contract. Npgsql documents that keepalive sends queries and that minimum pool size affects idle connection retention; see [connection parameters](https://www.npgsql.org/doc/connection-string-parameters). No claim is made here about the values currently configured in production.

**12. Protect unsaved entry and close the known UI workflow gaps**

Evidence: [InvoiceViewModel.cs:614](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/Invoice/InvoiceViewModel.cs:614) clears immediately; loading another bill replaces the form. [MainWindowViewModel.cs:234](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/MainWindowViewModel.cs:234) switches straight into editing, and the title-bar close handler closes the window directly. Add an invoice dirty state and one awaitable discard prompt for clear, load-another-bill, and application close. A recoverable local, unnumbered scratch form could be a later product improvement; it must not become a second durable billing database or trigger Tally automatically.

Reuse the [existing UI audit](D:/Projects/Tally_Wrapper_V2/docs/audits/ShowroomBilling_UI_Findings_Audit_2026-09-01.md) as the canonical UI backlog. Current source still supports its findings about passive Tally checks, the Print Estimate button's status-only command, and page-local fully-posted-day filtering:

- [ShellHealthCoordinator.cs:26](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/ShellHealthCoordinator.cs:26) retains a 30-minute Tally poll; startup also requests Tally health. Reconcile this with the operator-only contract by removing passive Tally calls while keeping explicit health refresh/preflight.
- [InvoiceView.xaml:573](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/Views/Invoice/InvoiceView.xaml:573) binds the button to the Invoice VM fallback. Route the button and F9 through the same actual preview workflow.
- [BillsViewModel.cs:455](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/Bills/BillsViewModel.cs:455) calculates fully-posted dates from loaded items. Move any whole-day decision to a server query that sees all matching bills, with a test spanning pages.

The existing DPI changes were already in progress when this review began. They are not presented as a fresh defect or claimed verified. Each UI slice still needs launch-and-exercise validation, including keyboard use and supported DPI/window sizes.

**13. Bound expensive print preparation and allow printer rediscovery**

Evidence: [PrintDispatcher.cs:16](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/Services/PrintDispatcher.cs:16) renders printer pages at 600 DPI, collects all PNG pages, then builds a FixedDocument containing them. [PrintPreviewViewModel.cs:592](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ViewModels/Printing/PrintPreviewViewModel.cs:592) schedules the synchronous renderer in `Task.Run`; cancellation can discard the result but does not stop an already-running render loop.

Keep print quality, but serialize expensive rendering, check cancellation between documents/pages where supported, and bound how many high-resolution pages are prepared ahead. For scale context, an A4 page at 600 DPI is roughly 35 million pixels, or about 133 MiB at four bytes per decoded pixel; this is a calculation, not a measured memory profile. Validate large selections and rapid copy/layout changes with actual memory/latency measurements.

Printer discovery is cached for the singleton's lifetime, including an empty result after failure. Add an explicit refresh/invalidation path so installing a printer or restarting the spooler does not require restarting Billing. Cover this with a fake discovery provider and a Windows smoke test.

**14. Fix the two current engineering blockers first**

- Formatting fails at [PrintLayoutOptionsTests.cs:1](D:/Projects/Tally_Wrapper_V2/tests/ShowroomBilling.Tests/PrintLayoutOptionsTests.cs:1) and [PrintLayoutViewModelTests.cs:1](D:/Projects/Tally_Wrapper_V2/tests/ShowroomBilling.Desktop.Tests/PrintLayoutViewModelTests.cs:1). Order the imports and rerun the exact CI formatting command. The source build and tests passing do not override this CI failure.
- Both Desktop profiles in [.vscode/launch.json:22](D:/Projects/Tally_Wrapper_V2/.vscode/launch.json:22) point to `Billing.exe`; [ShowroomBilling.Desktop.csproj:6](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Desktop/ShowroomBilling.Desktop.csproj:6) produces `TallyWrapper`. Correct both paths and smoke-test F5 from a clean build. DEV_SETUP also names a compound launch target that differs from the checked-in target.

**15. Strengthen verification where the current green suite missed behavior**

Keep the existing contract and PostgreSQL jobs. Add complete business scenarios from this review, not merely more assertions about DTO shape. The nine existing Postgres cases are useful but do not cover the interleavings, cancellation, and suffix scenarios above.

The CI floor is 27% aggregate line coverage. Before raising it, report coverage by critical workflow and merge coverage across overlapping assemblies: [Test-Coverage.ps1](D:/Projects/Tally_Wrapper_V2/tools/Test-Coverage.ps1) currently sums each report's covered/valid totals, which can count shared production files twice. This pass did not measure a new coverage percentage. Prioritize meaningful transition and failure-path checks over an arbitrary larger aggregate.

Add a small isolated Windows UI smoke suite with stable automation identifiers for Save → preview → close/reset, failed-save preservation, admin unlock/expiry, keyboard routing, and server-mode switching. Exercise published artifacts separately: embedded API extraction, configuration sanitization, matching Desktop/API versions, native printing dependencies, and service install/repair on a disposable Windows environment. CI currently builds and tests source; it does not demonstrate those packaged flows.

For multi-counter support, include a trusted workstation/counter identity in audit context. [HttpAuditActorContext.cs:29](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Api/Security/HttpAuditActorContext.cs:29) currently records every normal device mutation as `device/desktop`, while client presence has richer transient information. Reuse registered identity rather than treating an arbitrary request label as authenticated identity. Expose the existing correlation ID in actionable Desktop errors and a redacted support export.

Turn the documented backup/restore responsibility into a repeatable restore drill with recorded outcomes; this source review cannot establish whether operational backups already exist. Also consider separate tray commands for “Close tray” and “Stop API service”: the current exit intentionally stops service for all counters. That is a product decision, not a defect claimed from an accidental code path.

**16. Cleanup should be narrow and evidence-based**

The scanner produced 85 heuristic candidates. Most are false positives from controller discovery, generated migrations, XAML code-behind, DTO filenames, and legitimate compatibility behavior. It even classified the actively used `CopyableMessageDialog.cs` as a backup because of its name. No blanket removal is justified.

| Candidate | Classification | Recommendation / verification |
|---|---|---|
| [SyntheticBatchCountBoundsResponse](D:/Projects/Tally_Wrapper_V2/src/ShowroomBilling.Contracts/Bills/SyntheticBatchContracts.cs:44) | Likely removable; low blast radius | Only the declaration appears in tracked source/tests/docs/tools; no endpoint returns it. Confirm no external consumer, then remove with a normal build/contract check. |
| Duplicate fiscal-year calculations | Consolidation; requires behavior checks | NumberingService and InvoiceNumberFormatter implement the same calculation. Choose one authoritative helper. Define whether backdated bills use bill date or save date and whether the showroom timezone governs the April boundary before changing policy. |
| Repeated stable showroom-ID calculation | Consolidation | Several services repeat the MD5-based deterministic ID mapping. Share the existing algorithm without changing any existing IDs; it is an identity mapping, not a password hash. |
| Large orchestration files | Targeted refactoring | Invoice VM: 777 lines; Bills VM: 768; print-preview VM: 756; BillDocument: 765 in this checkout. Extract focused save/recovery or print-preparation behavior when changing it, following the workflow helpers already present. File length alone is not grounds for a rewrite. |
| Stale graph artifacts | Regenerate when next needed | Rebuild the old graph with the current schema after changes settle; keep it ignored. Do not use the stale graph as deletion evidence. |
| Ignored build outputs and local files | No bulk deletion recommendation | They include credentials/configuration and useful local work, not just disposable cache. Remove only explicitly selected generated outputs when needed. |

Correct, rather than delete, the authoritative docs. Examples verified in this checkout:

- [DEV_SETUP.md:225](D:/Projects/Tally_Wrapper_V2/DEV_SETUP.md:225) says a four-character admin minimum; the service requires six.
- [DEV_SETUP.md:221](D:/Projects/Tally_Wrapper_V2/DEV_SETUP.md:221) still describes separate Fetch buttons, contrary to the consolidated refresh flow.
- [DEV_SETUP.md:227](D:/Projects/Tally_Wrapper_V2/DEV_SETUP.md:227) says 300 DPI while dispatch uses 600.
- [docs/README.md:3](D:/Projects/Tally_Wrapper_V2/docs/README.md:3) describes only one-box deployment; README now documents server mode too.
- [docs/04](D:/Projects/Tally_Wrapper_V2/docs/04_numbering_and_idempotency.md) mixes historical migration behavior, target design, and current guarantees. Clearly label history and fix the save replay promise when implementing finding 5.
- Retain the existing UI audit, design bundle, migrations, bill audit history, and mixed-format compatibility tests. They are relevant references or live behavior.

**Suggested implementation order**

1. Repair import ordering and F5 paths. Promote the six audit probes into focused regression tests with clear expected behavior; add Postgres versions where constraints or concurrency matter.
2. Fix revision claiming, failed-create editing, revise numbering, and canceled finalization. Keep each change small enough to review and validate independently.
3. Add save-operation identity and suffix-safe numeric numbering. Use this opportunity to measure numbering cost, with a reviewed migration/backfill and the full Postgres category.
4. Complete lease and shared-settings conflict handling; settle the one-writer versus multiple-writer recovery contract. Add batch-run recovery if that feature remains actively used.
5. Close the existing functional UI findings, protect unsaved forms, and bound printing. Update the canonical UI audit and operator docs in each pass.
6. Add packaged Windows smoke verification, support diagnostics, and restore-drill evidence; finish the verified small cleanup items.

No application source was changed as part of this review. The deliverables are this report and local audit probes/logs. A broad framework migration, extra Domain project, background voucher queue, or wholesale view rewrite is not needed to address the demonstrated issues.
