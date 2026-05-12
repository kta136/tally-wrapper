# Numbering and Idempotency

This document freezes invoice numbering, renumbering policy, Tally posting identity, and retry/replay handling for V2.

---

## 1. Current behavior that must be understood

### 1.1 Preview vs actual reservation

Current system behavior:

- invoice preview uses repository next-number logic
- preview does **not** reserve the final number used by save/post
- true allocation happens in the persistence path, not the label preview path

This distinction must be preserved conceptually in V2.

### 1.2 Current numbering side effects

Current migration-critical side effects:

- manual renumber can change the next auto-generated number
- manual renumber can reopen gaps
- deleting a middle bill can reopen gaps
- posted-bill renumber warns that Tally is not updated automatically
- delete-all-vouchers also resets local numbering side effects because local sequence state is removed

### 1.3 Current renumbering warnings

Current system warns when:

- the chosen number is already in use
- changing the number will alter the next auto-generated value
- changing the number on a posted bill requires repost to sync Tally-side numbering identity

---

## 2. V2 numbering design

### 2.1 Core design

V2 numbering is server-side only.

Rules:

- invoice number is reserved at draft creation so the operator can print and hand the customer a numbered estimate immediately
- preview is informational only (what number the next *new* draft would receive)
- reservation is idempotent per bill: `CreateDraftAsync` uses `draft:{billId}` as the reservation key, so a retried save returns the same number
- the reservation lives on the bill through every transition — push does not issue a new number, it just transitions the bill to `posting` and synchronously calls Tally; on success the number is preserved on the resulting `posted` bill
- `UpdateDraftAsync` never changes the number once assigned

### 2.2 Sequence scope

Numbering scope should be keyed by:

- `tenant_id`
- `showroom_id` or numbering branch scope
- `fiscal_year`
- `document_type`

Recommended table key:

- `(tenant_id, showroom_id, fiscal_year, document_type)`

### 2.3 Preview behavior in V2

Preview endpoint returns a non-reserved next visible number.

It is allowed to differ later if another counter finalizes first.

That is acceptable because preview is not reservation.

### 2.4 Manual renumbering (admin-only)

`POST /api/bills/{billId}/change-number` (`[Authorize(Policy = "Admin")]`, requires `X-Admin-Token`) lets an operator rewrite `bill.InvoiceNumber` after creation. Allowed on any state except `posting`. The endpoint:

- **Accepts digit-only input.** `NewInvoiceNumber` must be pure ASCII digits parseable as a positive `long`; the server auto-formats via `InvoiceNumberFormatter.Format(prefix, suffix, core, bill.FiscalYear)` using the same cloud-settings prefix/suffix a fresh reservation would use. The Desktop's Change-Number dialog previews the formatted result live.
- Enforces the existing `(ShowroomId, FiscalYear, InvoiceNumber)` unique index — 409 on collision.
- Returns three informational flags:
  - **`LeavesGap`** — chosen number is higher than the sequence's `NextValue`, so a gap will appear in local history. Sequence `NextValue` is **not** advanced on change-number; the forward-skip in `ReserveAsync` (§2.5) handles the new occupied core on the next allocation.
  - **`TallyDiverges`** — bill already touched Tally (`posted` or `failed`) — the Tally voucher (if any) still carries the old number. Admin attests to reconcile in Tally manually.
  - **`ReservationOrphaned`** — the original draft reservation (idempotency key `draft:{billId}`) is kept in place so retried drafts can't accidentally collide. The reservation's `FormattedNumber` now differs from the live bill.
- **Trailing rename rolls `NextValue` back.** When the change moves the trailing bill *down* (e.g. `94 → 92` when `94` was the most recent reservation), the commit branch calls the same `RollbackTrailingSequenceAsync` the delete path uses, inside an explicit transaction shared with the save. `NextValue` becomes `min(currentNextValue, max(parsed-trailing-digits across remaining bills in scope) + 1)` — see §2.6. Moving the number *up* is a no-op for the rollback (no trailing core was freed); the forward-skip handles it on next allocation. The response's `SequenceNextValue` is re-read after rollback so callers see the post-state value.
- Supports `DryRun=true` for a validate-only round-trip; the Desktop uses this to surface warnings before the real commit.
- Writes `bill.number.changed` audit event with old/new/flags/reason.

### 2.5 Forward-skip at reservation time

`NumberingService.ReserveAsync` starts at `InvoiceSequences.NextValue` but **skips forward** past any core value whose **parsed trailing digits** already appear in `bills.InvoiceNumber` for the same `(ShowroomId, FiscalYear)`. This protects against collisions when an admin has moved a bill's number ahead via change-number (e.g. `0003 → 0010`); the allocator advances past `0010` rather than crashing on the unique index when it eventually reaches that core. Parsing trailing digits (rather than comparing to a freshly-formatted string) means a scope with historical mixed formatting — e.g. legacy `DDAJR/26-27/49` coexisting with newer `DDAJR/26-27/0049` — collapses both to the same core (`49`), so the allocator never re-issues a semantically-occupied number just because its formatting drifted from what `InvoiceNumberFormatter` produces today.

Properties preserved:

- **Mid-range gaps stay gaps** — if bill `0007` is deleted while `0008..0012` still exist, the allocator does not backfill `0007` on the next reservation; it advances from the current `NextValue`.
- **Trailing freed cores are reclaimed on delete** — see §2.6.
- **Reservations don't block reuse** — only `bills.InvoiceNumber` is scanned. Orphaned reservations (from `ReservationOrphaned` above) deliberately don't block; they exist only to idempotency-lock `draft:{billId}` retries.
- **Bounded cost** — a safety cap of 1000 iterations guards against pathological cases; each iteration is one indexed `EXISTS` query against the unique partial index.

`GetPreviewAsync` applies the same skip so the preview matches what a real reservation would allocate.

---
### 2.6 Sequence rollback on trailing delete or rename

When a bill is deleted (`BillAdminWorkflow.DeleteAsync`) **or its number is moved down via change-number** (`BillAdminWorkflow.ChangeInvoiceNumberAsync`, §2.4), the allocator recomputes `NextValue` as `min(currentNextValue, max(parsed-trailing-digits across remaining bills in scope) + 1)`. So if `NextValue = 47` and bill `0046` is deleted while bill `0045` still exists, `NextValue` becomes `46`. If both `0045` and `0046` are deleted in the same selection, the second iteration recomputes the max across what remains and `NextValue` settles at `45`. The next reservation then reuses `0045`. The same logic fires when an operator renames `0046 → 0042`: the rename's reads-after-save include the bill at its new core (`0042`), `max(remaining) = 0045`, and `NextValue` rolls back to `0046` so the freed core is reclaimed.

Properties:

- **Mid-range deletes leave `NextValue` alone.** Deleting bill `0040` while `0041..0046` still exist keeps `max = 46`, so `NextValue` stays at `47`. Mid-range gaps remain permanent; only trailing deletes (and deletes that expose a previously-stale `NextValue`) move the counter.
- **Format-tolerant.** The check parses trailing digits of `bills.InvoiceNumber` rather than comparing to a formatted string. Historical mixed-format scopes (e.g. legacy `DDAJR/26-27/49` coexisting with newer `DDAJR/26-27/0049`) collapse to the same core (`49`), so the rollback never walks past a genuinely occupied core just because its formatting drifted from what `InvoiceNumberFormatter` produces today.
- **Anchors a stale `NextValue` on first delete.** If `NextValue` was sitting higher than the actual occupied range (e.g. the prior bill was deleted before this feature shipped, or after a manual sequence bump that no bill ever consumed), the next delete in scope pulls `NextValue` down to `max(parsedCore) + 1`. This is intentional — the alternative would be a permanent skip past cores that have no remaining bill evidence.
- **Atomic with the delete.** Both run inside the same transaction, with a `FOR UPDATE` lock on the `invoice_sequences` row so a concurrent reservation cannot allocate a core the rollback is about to free.
- **Audit-logged.** A single `numbering.sales_invoice.rolled_back` event is written per delete that actually moves `NextValue`, recording `from`, `to`, `maxOccupiedCore`, and `triggeredByBillId`.
- **InMemory test provider** forks to a plain `FirstOrDefaultAsync` (no `FOR UPDATE`); production race semantics are not exercised in unit tests. Real concurrency requires a Postgres test harness.

---

## 3. Uniqueness rules

### 3.1 Hard uniqueness

The database must enforce uniqueness on issued numbers.

Recommended uniqueness:

- `(tenant_id, fiscal_year, document_type, invoice_number)` unique

### 3.2 No duplicate issued numbers

Guaranteed:

- two finalized bills cannot receive the same issued invoice number

### 3.3 Gaps policy

V2 does **not** promise literal gapless numbering.

V2 guarantees instead:

- no duplicates
- no invisible orphan numbers
- every issued number maps to an auditable bill record
- voided/cancelled issued numbers remain traceable

This is an intentional redesign away from the current local gap-reuse side effects.

---

## 4. Manual renumber policy

### 4.1 Current system

Current system allows manual renumber for:

- `draft`
- `pending_sync`
- `sync_failed`
- `sync_review`
- `posted`

### 4.2 V2 policy

V2 redesign:

- normal operator renumber is allowed only while the bill is still `pending`
- after push/finalization, the invoice number is immutable
- if a business correction is required after finalization, use one of:
  - void + reissue
  - revise/correct workflow that creates a new document lineage
  - tightly controlled admin correction workflow with explicit audit trail

### 4.3 Why this changes

This is a deliberate redesign.

Reasons:

- cloud-owned numbering
- immutable finalized snapshots
- better accounting traceability
- removal of current side effects where renumbering reopens gaps and changes future numbering unexpectedly

---

## 5. Tally posting identity

### 5.1 Separate concepts

Do not collapse these concepts:

- business document identity
- invoice number
- cloud idempotency key
- Tally `REMOTEID`

They are related but not interchangeable.

### 5.2 Recommended identifiers

| Identifier | Scope | Stability |
|---|---|---|
| `bill_id` | Business document lineage | Stable |
| `bill_revision_id` | Immutable commercial snapshot | Stable per revision |
| `invoice_number` | Commercial numbering | Stable after finalization |
| `posting_job_id` | Queue/job execution | Stable per queued posting request |
| `idempotency_key` | Cloud dedupe/replay guard | Stable per posting intent |
| `REMOTEID` | Tally import identity | Fresh when fallback changes XML path or when re-attempt policy requires it |

---

## 6. Idempotency key strategy

### 6.1 Primary idempotency key

Recommended V2 key:

`showroom_id + bill_id + bill_revision_id + operation_type`

Examples:

- `showroomA/bill_42/rev_7/post`
- `showroomA/bill_42/rev_7/repost`

### 6.2 Why revision-based keys

The immutable bill revision is the unit of posting truth.

If content changes materially, create a new revision and therefore a new posting identity.

### 6.3 Operation types

Recommended operation types:

- `post`
- `repost`
- later, if needed: `cancel`, `credit_note`, `reversal`

---

## 7. `REMOTEID` linkage rules

### 7.1 Golden-path rule

The XML golden path must be preserved:

- plain XML first
- plain no-batch fallback second
- do not blindly reuse `REMOTEID` after failed shape changes

### 7.2 V2 `REMOTEID` policy

Implemented V2 `REMOTEID` content:

- exactly the posting job's idempotency key
- stable for the lifetime of that queued posting job
- different for reposts because repost creates a new posting job

### 7.3 Why not use invoice number alone

Invoice number alone is not enough because:

- reposts are possible
- fallback retries may need a fresh `REMOTEID`
- ambiguous outcomes require safe reconciliation against both business and attempt identity

---

## 8. Replay / retry / ambiguity handling

### 8.1 Retryable failures

Retry when `ITallyPoster` sees:

- Tally host unreachable
- timeout
- connection refused
- unreadable transport response with no reliable business outcome

### 8.2 Non-retryable failures

Fail immediately for:

- missing ledger
- missing stock item
- missing voucher type
- company mismatch
- period lock
- business validation rejection

### 8.3 Ambiguous outcomes

If the request may have reached Tally but success is uncertain:

1. do not blindly resend immediately
2. reconcile first using deterministic lookup rules
3. only retry if the voucher is not found

### 8.4 Reconciliation lookup order

Recommended order:

1. lookup by `REMOTEID` if available in Tally-side inspection path
2. lookup by invoice number + voucher type + company
3. if found, mark job as posted and store reconciliation note
4. if not found, leave the job failed and require an explicit operator retry/repost

---

## 9. Example numbering formats

### 9.1 Preview examples

- `INV-145/26`
- `C1-00981`
- `2026-27/000145`

### 9.2 Reserved final number examples

- `INV-146/26`
- `C1-00982`

These are examples only. Final format depends on configured prefix/suffix and showroom numbering policy.

---

## 10. Implementation notes

### Preserve from current system

- preview is not reservation
- operators must still see a previewed next number
- numbering warnings matter historically and must not disappear without replacement
- posted-number changes are accounting-sensitive

### Intentional redesign in V2

- no renumbering of finalized documents in normal operator flow
- no gap reuse as a normal side effect of delete/renumber
- cloud database owns numbering state and idempotency state
- `REMOTEID` is treated as Tally transport identity, not as the only dedupe key
