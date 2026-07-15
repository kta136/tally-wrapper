# CLAUDE.md

Notes for Claude (or any AI coding agent) working in this repo. Read [README.md](README.md) and [DEV_SETUP.md](DEV_SETUP.md) first — those describe the system; this file describes how to *work* on it.

## What this repo is

Two-process Windows desktop billing app for a jewellery showroom. Desktop (WPF, `net10.0-windows`) drives a local ASP.NET Core API (`net10.0`), which owns Postgres (Aiven-managed PostgreSQL) and dials TallyPrime's local XML endpoint. The Desktop owns the API lifecycle via a Job Object so orphaned processes aren't a class of bug.

- **Solution:** `ShowroomBilling.sln` · target framework is **`.NET 10`** across every project (Desktop is `net10.0-windows`).
- **Hosts:** `src/ShowroomBilling.Api`, `src/ShowroomBilling.Desktop`.
- **Layers:** `Contracts` (DTOs, shared between hosts), `Application`, `Infrastructure` (EF Core + Tally XML), `Printing` (QuestPDF templates). (There is no `Domain` project — business rules live in `Application` / `Infrastructure`.)
- **Tests:** `tests/ShowroomBilling.Tests` (API + infra) and `tests/ShowroomBilling.Desktop.Tests` (ViewModels).

## Authoritative docs — pointers, not restatements

| Topic | File |
|---|---|
| Dev prerequisites, build, run, Aiven connection strings | [DEV_SETUP.md](DEV_SETUP.md) |
| Bill state machine (draft → pending → posting → posted/failed/voided) | [docs/03_bill_state_machine.md](docs/03_bill_state_machine.md) |
| Numbering & idempotency (reservation, `idempotency_key`, `draft:{billId}`) | [docs/04_numbering_and_idempotency.md](docs/04_numbering_and_idempotency.md) |
| Tally integration responsibility split (sync, operator-initiated) | [docs/05_tally_integration_contract.md](docs/05_tally_integration_contract.md) |
| API surface | [docs/09_api_spec.md](docs/09_api_spec.md) |
| DB schema | [docs/10_database_schema.md](docs/10_database_schema.md) |
| Deployment topology, recovery, child-process supervision | [docs/11_deployment_and_ops.md](docs/11_deployment_and_ops.md) |
| Settings storage contract (cloud-owned vs local-only) | [docs/14_settings_storage_contract.md](docs/14_settings_storage_contract.md) |
| **UI design reference + token → WPF mapping** | [docs/15_ui_design_reference.md](docs/15_ui_design_reference.md) |
| Design bundle (HTML/CSS/React mockup) | [docs/design/](docs/design/) |

If you're about to write a paragraph that duplicates one of these, link instead.

## Workflow rules

- **Smoke-test after every slice for WPF work.** `dotnet build` validates XAML compiles — it does not validate binding paths, layout, visibility triggers, or keyboard flow. Launch the exe and exercise the changed feature before claiming done.
- **Update docs in the same pass as code.** If a slice changes a setting, endpoint, shortcut, or user-visible flow, edit `docs/` and `DEV_SETUP.md` alongside the code. Don't leave them drifted.
- **Scope discipline.** For refactors or UI audits, prefer the smallest visual/logical change that closes the gap. No pre-emptive abstractions, no unrelated cleanup, no renaming "while you're in there".
- **Ask before rebuilding at scale.** If a request could mean "touch a handful of files" or "rewrite every view", use `AskUserQuestion` before starting. The blast radius matters.
- **Never `--no-verify` or skip pre-commit hooks.** If a hook fails, fix the cause; don't bypass.

## Design system — non-negotiables

The design target is the hi-fi prototype in [docs/design/](docs/design/), distilled into [docs/15_ui_design_reference.md](docs/15_ui_design_reference.md). Tokens live in [Resources/DesignTokens.xaml](src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml); control styles in [Resources/Styles.xaml](src/ShowroomBilling.Desktop/Resources/Styles.xaml).

- **Always use tokens, never hardcode hex.** If a color isn't in the token sheet, add it to `DesignTokens.xaml` — don't inline `#FF...` in a view.
- **Typography is Windows-native.** `UiFontFamily` → `Segoe UI Variable Display, Segoe UI Variable, Segoe UI`. `MonoFontFamily` → `Cascadia Mono, Consolas`. The design calls for Inter/JetBrains Mono; those aren't installed on Windows so we use the closest native equivalents. Don't reintroduce Inter/JB Mono unless you're also bundling the TTFs.
- **Accent color is `#4F46E5`** (Tailwind indigo-600) — this matches the design bundle's own SVG thumbnail, not the muted oklch conversion we had earlier.
- **Dialog chrome pattern:** scrim `#61152238`, `DropShadowEffect BlurRadius="32" Opacity="0.22" ShadowDepth="6" Color="#0F172A"`, `dialog-head` with padded border-bottom, `dialog-foot` with `BgSunkenBrush` and `DividerBrush` top border. Follow [AdminUnlockDialog.xaml](src/ShowroomBilling.Desktop/Views/Admin/AdminUnlockDialog.xaml) or [SystemHealthDialog.xaml](src/ShowroomBilling.Desktop/Views/SystemHealthDialog.xaml) as the reference.
- **Dialogs are `UserControl` overlays, never `<Window>` modals.** All dialogs are hosted once in [MainWindow.xaml](src/ShowroomBilling.Desktop/MainWindow.xaml) and switched on/off via `MainWindowViewModel.ActiveDialog` (a string) + `DialogVisibilityConverter` with a per-dialog `ConverterParameter`. For *awaitable* prompts (change-number, reason, admin unlock), use a `TaskCompletionSource<T>` kept on `MainWindowViewModel`: the VM configures the dialog VM, sets `ActiveDialog`, and `await`s the TCS; the dialog's OK/Cancel commands fire a `Closed` event that resolves the TCS. See [MainWindowViewModel.PromptChangeNumberAsync](src/ShowroomBilling.Desktop/ViewModels/MainWindowViewModel.cs) / [`ChangeNumberDialogViewModel`](src/ShowroomBilling.Desktop/ViewModels/Bills/ChangeNumberDialogViewModel.cs) for the canonical pattern. `CloseDialogCommand` (Esc) must resolve any in-flight TCS as "cancel" or the awaiter hangs.
- **Status chips:** 18 px tall, `radius 2`, uppercase mono text, `Soft`/`Ink` token pair (e.g. `WarnSoftBrush` background + `WarnInkBrush` foreground). Bill state, LOCKED/UNLOCKED, ACTIVE/NONE, PENDING/IN SYNC all use this same pattern.
- **Field labels:** uppercase, `11.5 px`, `InkMutedBrush`, `Medium`. Use the `FieldLabel` style rather than redefining inline.
- **Status dots:** `7×7` (design `.dot`). Timeline/checklist bullets: `12×12` (design `.timeline .bullet`).
- **Banners (warn/err/info):** colored background + matching border + icon + `Ink` variant text. See `AdminUnlockDialog` for the pattern.

## EF migration gotcha

When running migrations against Aiven or any managed PostgreSQL database, pass the connection string explicitly:

```powershell
dotnet ef database update --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --connection "<postgres-connection-string>"
```

`ASPNETCORE_ENVIRONMENT` is **silently ignored** by the EF tools and falls back to the localhost default in `appsettings.json` — which will either fail loudly or (worse) quietly migrate a local dev DB you didn't mean to touch. Always supply `--connection`.

## Build & test

```powershell
dotnet restore ShowroomBilling.sln
dotnet build ShowroomBilling.sln
dotnet test ShowroomBilling.sln
```

- **Desktop DLL lock on rebuild:** if the Desktop exe is running, MSBuild fails to copy `ShowroomBilling.Printing.dll` into `bin/Debug/net10.0-windows/`. This isn't a compile error — just close the running app and rebuild.
- **VS Code F5:** `Foundation: Desktop + API` compound launch in `.vscode/launch.json` is the usual target.

## Tally integration reality check

- **Synchronous, operator-initiated.** No queue, no background worker, no retry timer. Push = one HTTP round-trip to Tally. Master refresh = one HTTP round-trip to Tally. If an operation isn't user-triggered, it shouldn't be talking to Tally.
- **Retry pipeline:** safe `ITallyXmlClient` reads have a Polly v8 retry (2 attempts, ~200 ms jittered exponential backoff) for `HttpRequestException` + 5xx. Voucher writes are marked and explicitly excluded: one operator action makes at most one voucher HTTP attempt. A transport timeout/error/unreadable response after the write begins is ambiguous and moves the bill to `reconciliation_required`; explicit Tally `LINEERROR` content moves it to `failed`.
- **Recovery:** `StuckPostingRecoveryHostedService` moves any bill stuck in `posting` to `reconciliation_required` on the next API boot. The operator must verify Tally and use admin Mark as Pushed or Mark as Pending before another write.
- **The only write actions:** Save Bill (Invoice tab) and Push / Retry / Repost / Revise / Void / Edit (Bills tab). Printing never mutates bill state.

## Error response contract

API errors flow through a single pipeline:

- `DomainExceptionHandler` (in `src/ShowroomBilling.Api/Middleware/`) is an `IExceptionHandler` that maps known domain exceptions to HTTP status + RFC 7807 ProblemDetails. Mappings: `BillNotFoundException` / `DraftLeaseNotFoundException` → 404, `BillStateConflictException` / `AdminPasscodeNotConfiguredException` → 409, `DraftLeaseOwnershipException` → 403, `AdminPasscodeInvalidException` → 401, `ArgumentException` → 400. Everything unmapped falls through to the default 500.
- **One body-shape exception:** `DraftLeaseConflictException` keeps emitting the richer `DraftLeaseConflictResponse { Error, ExistingLease }` shape because the Desktop's `DraftLeaseApiClient` parses it as a typed record. Don't fold this one into ProblemDetails without updating the client.
- Controllers should **not** have per-action `try/catch` around domain exceptions. Let them bubble. The only legitimate controller-level error returns are null-request-body `BadRequest` guards (the binder can leave `request` null when the body is empty) and result-is-null `NotFound()` for read endpoints.
- On the Desktop side, API clients read ProblemDetails `detail` / `title` for non-2xx responses.

## Admin flow

Press `~` (backtick) anywhere to open `AdminUnlockDialog`. Admin token lives in `AdminTokenStore` (30 min TTL), rides as `X-Admin-Token` on admin-gated endpoints, and is attached by `DraftLeaseApiClient`, `BillsViewModel.AdminUnlockHandler`, and the Database settings save path. If an admin call is made while locked, the unlock dialog opens and the call retries on close.

**Bootstrap (no passcode configured yet).** `AdminUnlockDialog` runs `LoadStatusCommand` on open and switches between two locked-state forms via `ShowUnlockForm` / `ShowInitialSetupForm` on `AdminUnlockViewModel`: when `IsPasscodeConfigured == false` it shows a "Set passcode" form (NEW + CONFIRM only, no current passcode required); on save the VM's initial-setup branch auto-unlocks the session. The Settings → Admin tab is gated on `IsAdminFeaturesVisible` (i.e. only appears once unlocked), so this dialog is the only in-app surface for the very first passcode — subsequent change-passcode lives in Settings → Admin. Min length is **6** (server's `AdminAuthService.MinPasscodeLength`); the VM mirrors that — keep them in sync if you change one.

Server side, the token is validated by `AdminAuthenticationHandler` (registered as scheme `AdminToken`) and gated via `[Authorize(Policy = AdminPolicy.PolicyName)]` — not the old `[RequireAdmin]` filter. `HttpContext.Items[AdminTokenConstants.HttpContextItemKey]` is still populated for compatibility with `AdminController.GetSession()`; downstream code can also read the `ClaimsPrincipal`.

**Admin unlock throttling.** `IAdminUnlockThrottle` (singleton) gates `UnlockAsync`: 4 free attempts, then exponential backoff (2s/4s/8s/16s, capped at 30s) per showroom. Throttled requests throw `AdminUnlockThrottledException` → `429 Too Many Requests` with a `Retry-After` header. Counter is in-memory only — it resets on API restart by design (the realistic attacker is a forgetful operator, not a network brute-force; device-token auth in slice 2c already blocks other local processes from reaching this endpoint). Min passcode length is 6 chars on `SetPasscodeAsync`. Each failed unlock writes an `admin.unlock.failed` audit row.

**Device-token layer (separate from admin).** A second auth layer, `X-Device-Token`, gates every **mutating** endpoint against other local processes on a shared Windows machine. The token is a 32-byte random secret in `%LOCALAPPDATA%\ShowroomBilling\device_token.txt`; Desktop generates it on startup (`DeviceTokenProvider.GetOrCreateToken()` in `App.xaml.cs`) **before** spawning the API child, and API reads the same file via `DeviceTokenStore`. `DeviceTokenHandler` is a `DelegatingHandler` attached to every typed `HttpClient` in the Desktop DI graph, so every outbound call automatically carries the header. On the server, mutating controller methods are annotated `[Authorize(Policy = DevicePolicy.PolicyName)]`. **Admin-gated routes keep only `[Authorize(Policy = AdminPolicy.PolicyName)]`** — the admin passcode is the stronger secret; stacking both policies would be defensive noise with no meaningful security gain. Reads are unauthenticated. When adding a new mutating endpoint, annotate with the device policy; when adding a new admin-only endpoint, annotate with the admin policy only.

## Server-side payload validation

`BillValidator` (Application layer) runs on every client-supplied `BillPayloadDto` before persistence on `CreateDraft*`, `UpdateDraft`, and `Revise` (the latter only when `InitialPayload` is non-null — a replay from a prior revision skips re-validation). It caps payloads at 500 lines, bounds text and per-line `RawJson` (valid JSON, ≤64 KiB), rejects negative optional jewellery inputs, and enforces the summation-level invariants a client cannot forge: positive quantity, non-negative rate/line-total, `|RoundOff| ≤ ₹1`, `GrandTotal > 0`, non-negative discount/tax, `BillDate` within `today − 1 year` to `today + 1 day`. The two money-path invariants remain: (a) `Σ LineTotal ≈ GrandTotal + Discount − RoundOff`; (b) `GrandTotal ≈ Subtotal − Discount + Tax + RoundOff`. **Do not validate `Subtotal == Σ LineTotal`** or `LineTotal == Rate × Quantity`; jewellery pricing makes both invalid assumptions. Add new invariants to `BillValidator.Validate`, not controllers/services.

## Startup hosted-service resilience

`DatabaseInitializationHostedService` and `StuckPostingRecoveryHostedService` both wire into `StartAsync` and previously could block / fail the API boot if Postgres was slow or unreachable. Both run under bounded timeouts (DB init 15s, recovery 30s) using `CancellationTokenSource.CreateLinkedTokenSource(ct).CancelAfter(...)`. Failures are caught, logged at warning level, and recorded on `IStartupStatus` (an in-process singleton). The API continues to boot even if either service fails. `GET /api/health/startup` exposes the snapshot — Desktop reads this to surface a "limited mode" banner when `databaseReady = false`. No persisted state; the snapshot resets on every API boot.

**Recovery is fire-and-forget.** `StuckPostingRecoveryHostedService.StartAsync` does **not** await the scan — it kicks the work onto a `Task.Run` and returns `Task.CompletedTask` immediately, so it doesn't add to API boot. Inside the background task it first awaits `IStartupStatus.WaitForDatabaseReadyAsync(ct)` so a freshly-bootstrapped API doesn't hit an unmigrated schema. The wait faults if `RecordDatabaseFailure` was called; recovery treats that as "skip this boot" rather than logging the same DB error twice. `StopAsync` cancels and waits up to 2s for the in-flight task to drain. If you add another startup hosted service that depends on the database being migrated, await `IStartupStatus.WaitForDatabaseReadyAsync` rather than relying on registration order. Otherwise follow the same pattern: bound the work, never throw, write to `IStartupStatus`.

## Audit history is append-only

Bill audit rows are never purged during edit-and-repush and are not cascade-deleted with the bill. Each edit, push, definite failure, ambiguous outcome, manual resolution, and hard delete appends another event. `BillAuditStore.GetAuditAsync` reads by entity ID even after the bill row is gone, so the `bill.deleted` tombstone and full prior history remain queryable. Request-scoped audit writers use `IAuditActorContext`: device-authenticated mutations record `device/desktop`, admin mutations record `admin/<actor>`, and startup/background work records `system`.

## Push concurrency guard

`BillPostingWorkflow.PushInternalAsync` flips the bill to `posting` via a conditional `UPDATE` gated on `pending`/`draft`/`failed` (plus `posted` only for explicit repost), not a tracked-entity mutate + `SaveChanges`. If 0 rows are affected the request short-circuits without a second Tally call. The flip is deliberately **not** transactional with Tally so a crash leaves visible evidence for reconciliation. InMemory tests use a tracked fallback; Postgres integration tests cover the relational path.

**Desktop admin-gating pattern: hide admin-only affordances when locked.** Don't show buttons that will fail auth — the operator gets no feedback. Two VM-side flags drive visibility:

- `SettingsViewModel.IsAdminFeaturesVisible` — tracks `AdminVm.IsUnlocked`. Used by the Admin settings tab (sidebar entry is added/removed dynamically) and the Batch Data Scheduler group in Settings → Admin.
- `BillsViewModel.IsAdminUnlocked` — subscribes to `AdminTokenStore.Changed` and raises `PropertyChanged`. Used by top-toolbar Delete Selected, footer Change Bill Number, and admin-only context menu items.

Inside the Bills row context menu, `AncestorType=UserControl` walks into a popup tree and fails. The row `Border` sets `Tag="{Binding DataContext, RelativeSource={RelativeSource AncestorType=UserControl}}"` to hand `BillsViewModel` down, so menu items bind via `{Binding PlacementTarget.Tag.IsAdminUnlocked, RelativeSource={RelativeSource AncestorType=ContextMenu}}`. Keep that idiom when adding more admin-gated context-menu entries (currently: Change Bill Number, Mark as Pushed, Mark as Pending, Delete Local).

A small `ADMIN` chip in [NavBarView.xaml](src/ShowroomBilling.Desktop/Views/NavBarView.xaml) lights green when unlocked and invokes `LockCommand` on click — mirrors the CLOUD/TALLY health pills.

## Transactions in `BillService`

Most command methods are a single `SaveChangesAsync` — EF wraps that in an implicit transaction, so they're atomic without extra work. Two methods deliberately open an explicit transaction because they have multiple DB round-trips against the same `ShowroomBillingDbContext`:

- `CreateDraftCoreAsync` wraps `INumberingService.ReserveAsync` + bill/revision/audit persist. `NumberingService.ReserveAsync` detects an ambient transaction on the DbContext and enlists in it rather than opening its own — so the reservation and the bill commit together.
- `DeleteAsync` wraps `ClearSupersededByReferencesAsync` (immediate `ExecuteUpdateAsync`) + the bill delete.

**Do NOT wrap `PushInternalAsync` or `RepostAsync`'s pre-flip save in a single transaction.** Those are intentionally split so `posting` is visible to `StuckPostingRecoveryHostedService` if the API crashes mid-push. Wrapping them would defeat crash recovery.

Audit payloads are built via the typed `WriteAudit(billId, eventType, state, at, object? details)` overload in `BillService`. **Do not build audit JSON by string-concat** — the old pattern had escape bugs where an invoice number or reason containing a quote would break the JSON silently. The wire shape is `{ "state": "...", "details": { ... } }` and the Desktop's `BillDetailsViewModel` reads `details.reason` / `details.invoiceNumber` / `details.fiscalYear`; keep that.

## Shared constants and helpers you must use

- **Bill states:** [`ShowroomBilling.Contracts.Bills.BillStates`](src/ShowroomBilling.Contracts/Bills/BillStates.cs) is canonical, including `ReconciliationRequired`. Normal retry/edit/renumber/void/delete paths must not bypass that state; only admin Mark as Pushed/Mark as Pending resolve it. **Do not compare `bill.State` to string literals.**
- **Invoice number formatting:** [`ShowroomBilling.Contracts.Numbering.InvoiceNumberFormatter`](src/ShowroomBilling.Contracts/Numbering/InvoiceNumberFormatter.cs) is the single formatter used by server-side reservation (`NumberingService.ReserveAsync`), server-side change-number auto-format (`BillService.ChangeInvoiceNumberAsync`), and the Desktop `ChangeNumberDialog` preview. If you need to render or compare an invoice number, go through this helper — never hand-build `{prefix}{year}/{NNNN}{suffix}`.

## Numbering behavior worth knowing

- **Reservation forward-skips occupied cores.** `NumberingService.ReserveAsync` starts at `InvoiceSequences.NextValue` and advances past any core whose parsed trailing digits already appear in `bills.InvoiceNumber` for the same `(ShowroomId, FiscalYear)`. Protects against collisions when an admin has manually moved a bill via change-number, and against historical mixed-format scopes (legacy `/49` and newer `/0049` both parse to core `49`, so neither gets re-issued). Mid-range deleted gaps are not backfilled — the allocator advances from the current `NextValue`. Both the forward-skip and the post-delete rollback go through `InvoiceNumberFormatter.TryParseTrailingCore` — keep them in sync if you change one. Full detail in [docs/04_numbering_and_idempotency.md §2.5](docs/04_numbering_and_idempotency.md).
- **Trailing deletes roll `NextValue` back.** `BillAdminWorkflow.DeleteAsync` recomputes `InvoiceSequences.NextValue` as `min(currentNextValue, max(parsed-trailing-digits across remaining bills in scope) + 1)` — so deleting the most-recent bill (or a contiguous trailing run) reclaims those cores for the next reservation. Mid-range gaps are untouched (deleting bill `0040` while `0046` still exists keeps `NextValue` at `47`). The check parses trailing digits of `bills.InvoiceNumber` rather than comparing formatted strings, so historical mixed-format scopes (legacy `/49` alongside newer `/0049`) collapse correctly. Runs inside the delete transaction with `FOR UPDATE` on the sequence row; emits a `numbering.sales_invoice.rolled_back` audit event when it moves the value. InMemory test provider skips the lock — production race semantics aren't exercised in unit tests. See [docs/04 §2.6](docs/04_numbering_and_idempotency.md). When adding another path that **removes or moves** a bill out of the active sequence's trailing slot, route through `RollbackTrailingSequenceAsync` rather than re-inventing the algorithm.
- **Change-number accepts digits only, and moving the trailing bill down reclaims the freed core.** `ChangeInvoiceNumberAsync` auto-formats a digit-only `NewInvoiceNumber` via `InvoiceNumberFormatter`; non-digit values are rejected. After the save it calls the same `RollbackTrailingSequenceAsync` the delete path uses, inside an explicit transaction shared with the save — so renaming bill `94` to `92` while `94` was the most recent reservation rolls `NextValue` back from `95` to `94`, and the next bill picks up the freed slot. Moving a number *forward* (e.g. `92` → `200`) is a no-op for the rollback since `currentNext` is still ≤ `max(remaining) + 1`; the forward-skip in `ReserveAsync` handles that case on the next allocation. The dry-run branch deliberately reads the pre-change `seqNext`; the commit response re-reads the sequence after rollback so the surfaced `SequenceNextValue` is the post-state.

## Settings master-data refresh pattern

Each master section (Companies, Ledgers+VoucherTypes, Stock Items) has **one** `Refresh from Tally` button, not two. The button's command re-pulls from Tally and then immediately re-fetches the cached snapshot into the dropdown. `SettingsViewModel.LoadAsync` also auto-fetches all three snapshots on Settings load when their collections are empty, so the dropdowns are never empty on first visit. Don't reintroduce a separate "Fetch" button — the two-step flow was confusing and operators would forget the second click.

`POST /api/masters/refresh` returns `IReadOnlyList<TallyMasterRefreshResult>` — one entry per master fetched. The Desktop's `MastersApiClient.RequestRefreshAsync` returns the same shape; `SettingsViewModel.SummarizeRefresh` renders per-master counts on success and the first failing master on partial failure. **Don't deserialize the response as a single accept-shape record** — the older `MasterRefreshAcceptedResponse` was deleted because it silently swallowed real failures.

`TallyMasterRefresher.RefreshAllAsync` runs the four masters **sequentially**, each in its own DI scope. Tally's local XML endpoint is fragile under concurrent reads; fanning out to four concurrent calls used to amplify timeouts on slower Tally hosts. Per-master DI scoping survives so a mid-call failure doesn't poison the others.

## Test-harness reality

Tests in both `ShowroomBilling.Tests` and `ShowroomBilling.Desktop.Tests` use `Microsoft.EntityFrameworkCore.InMemory` for DB-backed scenarios. **InMemory does not simulate unique indexes, `FOR UPDATE` locks, transaction isolation, or race conditions.** So:

- Concurrency properties (e.g. "two `CreateDraftAsync` calls on different threads produce distinct invoice numbers") cannot be verified here — they need a real Postgres test harness (Testcontainers or a CI-side service).
- Unique-index violations (`(ShowroomId, FiscalYear, InvoiceNumber)`, etc.) silently pass in-memory.
- `NumberingService.ReserveAsync` short-circuits its transaction/`FOR UPDATE` path under InMemory. The separate `Category=Postgres` suite exercises relational behavior in CI against PostgreSQL 17; run `tools/run-postgres-tests.ps1` locally when Docker is available.

Treat green InMemory tests as "shape-correct", not "race-safe". Changes to numbering, locking, or unique-index-backed invariants must also pass the Postgres category suite.

**HTTP contract tests** (`tests/ShowroomBilling.Tests/Contracts/`) use `Microsoft.AspNetCore.Mvc.Testing` to boot the real API in-process via `TestApiFactory : WebApplicationFactory<Program>`. The factory swaps the DbContext to InMemory, forces `Database:AutoMigrateOnStartup = false`, and replaces `ITallyMasterRefresher` with `StubTallyMasterRefresher` so boot doesn't need real infrastructure. The real `DeviceTokenStore` is left in place — tests read the generated token via `factory.GetDeviceToken()`. When you add a new public-API endpoint or change a response shape, **add or update a contract test in this folder**: this is where the slice 1 master-refresh drift would have been caught at build time. `MasterRefreshContractTests` is the canonical example. To run them isolated: `dotnet test --filter "FullyQualifiedName~Contracts"`.

## Things I learned the hard way

Things past Claude sessions discovered; keep in mind:

- The design bundle's **thumbnail SVG** hardcoded `#4F46E5` while the CSS used `oklch(45% 0.14 258)`. The oklch value rendered muddy; the thumbnail hex is what visually matches the mockup. Prefer the hex.
- Inter / JetBrains Mono aren't installed on Windows. Our earlier font stack silently fell through to Segoe UI / Consolas — we just made that explicit.
- WPF dialogs that use a `Grid` with a scrim color (`#61152238`) + centered `Panel` border behave like modals inside the root control; they don't need a separate `Window`. Follow `AdminUnlockDialog` / `SystemHealthDialog`.
- Bool-backed state fields (e.g. `IsLocalDatabaseOverridePresent`) rendering as literal `True` / `False` in XAML is almost always a design bug. Replace with two `Border` chips gated by `BoolToVis` / `InverseBoolToVis`.
- **V1-ported constants are frozen.** The synthetic Batch Data Scheduler partitions bills in `[₹25,000, ₹1,99,000]`; ₹1,99,000 is a GST-compliance cap, not a preference. Do not relax it without product sign-off. See [docs/17_synthetic_batch.md](docs/17_synthetic_batch.md) and `SyntheticBatchPlanLimits`.
- **CommunityToolkit.Mvvm gotcha.** Setting an `[ObservableProperty]` inside a VM constructor triggers its `OnXxxChanged` partial. If that partial calls `SomeCommand.NotifyCanExecuteChanged()` and `SomeCommand` hasn't been assigned yet, you get a `NullReferenceException` in the ctor. Initialise commands *before* setting any observable property that has a partial-method hook.
- **`RelativeSource AncestorType=UserControl` resolves to the nearest UserControl *and reads its current DataContext*** — not the inherited one. If the UserControl has `DataContext="{Binding Foo}"` set on it in the host (e.g. `SettingsView DataContext="{Binding Settings}"` in MainWindow), then `AncestorType=UserControl` hits `SettingsViewModel`, not `MainWindowViewModel`. Silent bugs (missing command on wrong VM) result. Reach for `AncestorType=Window` when you need `MainWindowViewModel` from inside a nested view. Precedent: the `OpenSyntheticBatchCommand` button was silently inert for months because of this.
- **ContextMenu lives in a separate visual tree (popup).** `RelativeSource AncestorType=UserControl` from inside a `<MenuItem>` will NOT find the owning view. Use `PlacementTarget` to hop back into the main tree, and set `Tag="{Binding DataContext, RelativeSource={RelativeSource AncestorType=UserControl}}"` on the element hosting the `ContextMenu` to hand the VM through. Menu items then bind via `{Binding PlacementTarget.Tag.X, RelativeSource={RelativeSource AncestorType=ContextMenu}}`. See the Bills row context menu for the canonical case.
- **`KeyBinding Key="P"` without a modifier fires even while a TextBox has focus** (plain-letter keys aren't marked Handled by TextBox for routing purposes). Filter/search inputs can trigger parent commands mid-typing. Always pair bare-letter shortcuts with `Ctrl`/`Shift`/`Alt`, or skip the keybinding and rely on a button.
- **`dotnet build` fails with a file-lock error if the Desktop exe is running.** The DLLs compile fine — only the final exe copy breaks. Close the running app (or kill `ShowroomBilling.Desktop.exe`) and rebuild.
- **Reading `dotnet-trace` for an `async void` entry point is a trap.** `App.OnStartup` shows up with multi-second *inclusive* time in the trace, but that includes the entire async continuation that runs on the dispatcher message-loop *after* `window.Show()` has returned (via `MainWindow.OnLoaded` → `InitializeAsync`). The actual pre-window-paint cost is much smaller — verify with the `[startup-timing]` log line that `App.OnStartup` emits (Information level, written to `%APPDATA%\ShowroomBilling\logs\desktop-*.log`), not by reading inclusive ms off the speedscope flame graph. Phases logged: `embeddedExtract`, `hostBuild`, `hostStart`, `resolveWindow`, `showWindow`, `deviceToken`, `supervisor`, `windowVisibleAt` (total).
- **Device-token write + API child spawn run in parallel with `MainWindow` resolution.** `App.OnStartup` calls `DeviceTokenProvider.GetOrCreateToken()` and `ChildProcessSupervisor.Start()` on a `Task.Run` worker so they run concurrently with the UI-thread `GetRequiredService<MainWindow>()` + `InitializeComponent`. Order inside that task is preserved (`GetOrCreateToken` BEFORE `Supervisor.Start` — the API child reads the token file on its own startup). If you add another piece of pre-spawn work that the API depends on, put it inside the same `Task.Run` body in `App.OnStartup`, not inline before it. `MainWindow.OnLoaded` already TCP-probes for API readiness (`WaitForApiReadinessAsync`), so a slow spawn just delays the "ready" state — it doesn't break first paint.
- **`tools/publish-prod.ps1` ships with `PublishReadyToRun=true`** on both API and Desktop. R2R pre-JITs IL to native at publish time, costing ~10 % binary size and ~30 s of publish time, in exchange for a real cold-startup win — measured **~800 ms shaved off cold launch** and **~120 ms off warm launch** (`windowVisibleAt 2505 → 1706 cold`, `1244 → 1128 warm` against the Debug + Release-no-R2R baselines in `perf-traces/`). If you ever need to disable R2R for diagnostic purposes (e.g. to repro a JIT-only bug), strip the flag from both `$apiPublishArgs` and `$desktopPublishArgs` — don't half-disable it, the asymmetry will throw off any timing comparison.
