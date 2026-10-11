# Numbering and Idempotency

This document describes invoice numbering, renumbering, Tally posting identity, and how a repeated or uncertain request is handled.

---

## 1. Summary

- The API owns numbering. A bill gets its invoice number when it is first saved, in the same transaction as the bill. Push never issues a number.
- The preview on the Invoice tab reserves nothing.
- Numbers are unique per showroom and fiscal year. Numbering is not gapless: a number in the middle of the series that loses its bill stays unused while later numbers exist; a number freed at the end of the series is handed out again.
- A repeated Save is recognised by its client operation id and returns the bill it already created.
- A voucher write is sent to Tally once per operator action. An uncertain outcome is resolved by a person, never by a resend.

---

## 2. Numbering design

### 2.1 Core design

Numbering is server-side only.

- `CreateDraftAsync` (and `ReviseAsync`, for the new bill it creates) calls `NumberingService.ReserveAsync(documentType, fiscalYear)` inside the transaction that inserts the bill. It returns `ReservedNumber(FiscalYear, ReservedValue, FormattedNumber)`; the bill stores the formatted number in `InvoiceNumber` and the integer in `InvoiceCore`. If the bill cannot be saved the transaction rolls back and the number is not used up.
- The sequence row in `invoice_sequences` is read `FOR UPDATE`, so two saves cannot take the same value.
- `InvoiceNumberFormatter.Format` gives `{prefix}{fiscalYear}/{core}{suffix}`, the core zero-padded to `Numbering.InvoicePadding` digits (default 4): prefix `SR/` in fiscal year `2026-27` gives `SR/2026-27/0145`. When the prefix or suffix already carries the fiscal year (`DDAJR/26-27/`), the year is not repeated.
- Save is replay-safe through a client operation id: the Desktop generates `CreateBillDraftRequest.ClientOperationId` once per unsaved bill and reuses it on every retry. `bills.CreateOperationId` is unique, so a retry whose first attempt committed (response lost) returns that same bill and number; reusing the id with different contents is `409`. Requests without the id create a new bill every time. Draft updates carry `expectedRevisionId`, so a replayed update that already landed returns the stored bill instead of adding a revision.
- The number stays on the bill through every transition. Push moves the bill to `posting` and calls Tally; it does not issue a number. `UpdateDraftAsync` never changes the number.
- There is no reservation table. Builds up to 10 October 2026 wrote a row to `invoice_number_reservations` for every bill; the `DropInvoiceNumberReservations` migration removed it ([docs/10 §1.10](10_database_schema.md)).

### 2.2 Sequence scope

There is one sequence per `(ShowroomId, FiscalYear, DocumentType)`, the primary key of `invoice_sequences`. The app has one showroom (`Showrooms.DefaultId`) and one document type (`sales_invoice`).

**Which fiscal year.** A bill is numbered in the fiscal year of its **bill date** (`InvoiceNumberFormatter.ComputeFiscalYear(DateOnly)`), not the server clock: a 31 March bill entered on 1 April gets a number in the year that is closing. Where a "current" year is needed (preview without a date, status bar), it is read on the IST calendar, so the year turns over at 00:00 IST on 1 April rather than 05:30 IST. An edit that moves a bill's date into another fiscal year is rejected with 409; the operator keeps a date in the original year or creates a new bill.

### 2.3 Preview

`GET /api/numbering/preview` returns the number the next new bill would get, without reserving it. It applies the same forward-skip as a reservation (§2.5). Another counter may save first, so the number a bill finally gets can differ from the preview.

### 2.4 Manual renumbering (admin-only)

`POST /api/bills/{billId}/change-number` (admin policy, requires `X-Admin-Token`) rewrites a bill's number. It is allowed in any state except `posting` and `reconciliation_required`. The endpoint:

- **Accepts digits only.** `NewInvoiceNumber` must be ASCII digits that parse as a positive `long`; the server formats it with `InvoiceNumberFormatter.Format`, using the prefix, suffix and padding a new bill would get and the bill's own fiscal year. Anything else is refused with `409`. The Desktop's Change-Number dialog previews the formatted result live.
- Refuses a number or core that another bill of the same fiscal year already holds (`409`).
- Returns two informational flags:
  - **`LeavesGap`** — the chosen core is at or beyond the sequence's `NextValue`, so a gap appears in the series. `NextValue` is **not** advanced; the forward-skip in `ReserveAsync` (§2.5) passes the occupied core when the allocator reaches it.
  - **`TallyDiverges`** — Tally holds this bill's voucher under the old number (`BillTallyHistory.IsInTallyAsync`: the bill is `posted`, was edited after a push, or its latest Tally-truth audit event is a successful push or an admin Mark as Pushed). Committing the change then sets `EditedAfterPush`, moves a `posted` bill to `pending` and writes `bill.edit.reopened`, so the next push alters the same voucher to the new number.
- **Moving the trailing bill down frees its old number.** The save and `NumberingSequenceReclaimer.ReclaimAsync` (§2.6) run in one transaction: renaming `94 → 92` when `94` was the last number issued rolls `NextValue` back, and the next bill gets `94`. When Tally holds the bill, its old core is kept for Tally (§2.7). Moving a number up changes nothing in the sequence. The response's `SequenceNextValue` is read after the rollback.
- Supports `DryRun=true`: it validates and returns the flags without changing anything. The Desktop uses this to show warnings before the real commit.
- Writes a `bill.number.changed` audit event with the old number, the new number, both flags and the reason.

### 2.5 Forward-skip at reservation time

`NumberingService.ReserveAsync` starts at `InvoiceSequences.NextValue` but **skips forward** past any core value already stored in **`bills.InvoiceCore`** for the same `(ShowroomId, FiscalYear)`. Only the cores inside the 1000-value skip window are read, in one query through the `(ShowroomId, FiscalYear, InvoiceCore)` index. `InvoiceCore` is written by every path that sets a number (create, revise, change-number) and was backfilled by the `InvoiceCoreAndSequenceFloor` migration: reservation value when the bill still carried its reserved number, else the digits before the current suffix, else the trailing digits. Rows with no core (legacy custom strings) fall back to trailing-digit parsing (`InvoiceNumberFormatter.TryParseTrailingCore`). This protects against collisions when an admin has moved a bill's number ahead via change-number (e.g. `0003 → 0010`); the allocator advances past `0010` rather than failing on the unique index when it reaches that core. Comparing numeric cores (rather than freshly-formatted strings) means a scope with mixed formatting — e.g. `DDAJR/26-27/49` beside `DDAJR/26-27/0049` — collapses both to the same core (`49`), so the allocator never re-issues an occupied number because its formatting differs from what `InvoiceNumberFormatter` produces today.

Properties:

- **Mid-range gaps stay gaps** — if bill `0007` is deleted while `0008..0012` still exist, the allocator does not backfill `0007` on the next reservation; it advances from the current `NextValue`.
- **A number kept for Tally is passed over** like an occupied one — see §2.7.
- **Trailing freed cores are reclaimed** — see §2.6.
- **Bounded cost** — the window is 1000 values. If every core in it is occupied the save fails with an error instead of scanning further.

`GetPreviewAsync` applies the same skip so the preview matches what a real reservation would allocate.

---
### 2.6 Sequence rollback on trailing delete or rename

After a bill is deleted (`BillAdminWorkflow.DeleteAsync`) **or renumbered** (`BillAdminWorkflow.ChangeInvoiceNumberAsync`, §2.4), `NumberingSequenceReclaimer.ReclaimAsync` recomputes the sequence. The target is `max(MinNextValue, highest InvoiceCore still in the scope + 1)`; `NextValue` is set to the target when the target is lower, and is never raised. `MinNextValue` is first raised past the core of a deleted or renumbered bill whose voucher Tally holds (§2.7). So if `NextValue = 47` and bill `0046` is deleted while bill `0045` still exists, `NextValue` becomes `46`. If both `0045` and `0046` are deleted in the same selection, the second delete recomputes the highest core across what remains and `NextValue` settles at `45`. The next reservation then reuses `0045`. The same logic runs when an operator renames `0046 → 0042`: the highest remaining core is `0045`, and `NextValue` rolls back to `0046` so the freed core is reclaimed.

Properties:

- **Mid-range deletes leave `NextValue` alone.** Deleting bill `0040` while `0041..0046` still exist keeps the highest core at `46`, so `NextValue` stays at `47`. Only trailing deletes (and deletes that expose a stale `NextValue`) move the counter.
- **Format-tolerant.** The check compares stored numeric cores (`bills.InvoiceCore`) rather than formatted strings, so suffixes and padding changes never hide an occupied core. Rows without a stored core are read by their trailing digits.
- **Anchors a stale `NextValue`.** If `NextValue` sits higher than the occupied range (a sequence bump that no bill ever used), the next delete or rename in the scope pulls it down to the highest core + 1.
- **Atomic with the delete or rename.** Both run inside the same transaction, with a `FOR UPDATE` lock on the `invoice_sequences` row so a concurrent reservation cannot allocate a core the rollback is about to free.
- **Audit-logged.** A `numbering.sales_invoice.rolled_back` event is written each time `NextValue` moves, recording `fiscalYear`, `from`, `to`, `maxOccupiedCore` and `trigger` (`delete:{billId}` or `rename:{billId}`).
- **Only then.** Nothing reconciles the sequence at start-up: a number and its bill commit together, so `NextValue` can only run ahead of the bills after a delete or a rename, and both reclaim at once.

### 2.7 Numbers kept for Tally

A bill whose voucher Tally holds can lose its number here in two ways: the bill is deleted, or it is renumbered (Tally shows the old number until the next push alters the voucher). Issuing that number to a new bill would put two vouchers with one number in Tally, so the number is kept out of circulation. `ReclaimAsync` records it by raising `invoice_sequences.MinNextValue` to that core + 1:

- **Behind the sequence** (the usual case: the bill had a number the series issued), the floor stops every later roll-back from going back to it.
- **Ahead of the sequence** (the bill had first been renumbered upwards, say `0002 → 0009` while the series was at `0003`), the floor cannot help, because the series is walking towards that number. The reservation and the preview therefore treat the core just under the floor, `MinNextValue − 1`, as occupied and pass over it: the series runs `…0008, 0010`.

Limits, all from the floor being one value per sequence:

- The number stays kept even after the voucher is altered or deleted in Tally. The cost is one unused number.
- While the floor is ahead of the sequence, deleting the last bill does not roll `NextValue` back, so that delete leaves a gap.
- Only the highest kept number ahead of the sequence is passed over. If two Tally-held bills are both moved ahead of the series and both then deleted or renumbered again before the series reaches them, the lower number is issued when the series gets there. Tally then has two vouchers with that number, or refuses the push if it is set to prevent duplicates; change the new bill's number to resolve it.

---

## 3. Uniqueness and gaps

- `bills` has a unique index on `(ShowroomId, FiscalYear, InvoiceNumber)` for rows that have a number: two bills cannot hold the same number in one fiscal year.
- `bills.CreateOperationId` is unique for rows that have one: one Save creates one bill.
- Numbering is not gapless. A voided or revised bill keeps its row and its number. A deleted bill leaves a gap, unless it was at the end of the series (§2.6); its audit trail, ending in `bill.deleted`, stays readable by bill id.

---

## 4. Tally posting identity

| Identifier | What it identifies | Where it lives |
|---|---|---|
| Bill id | The bill | `bills.Id` |
| Revision id | One saved version of the bill's contents | `bill_revisions.Id` |
| Invoice number | The commercial number; sent to Tally as `VOUCHERNUMBER` | `bills.InvoiceNumber` |
| Client operation id | One Save from the Desktop | `bills.CreateOperationId` |
| Posting key | One revision's push; sent to Tally as `REMOTEID` on a create | built per request: `post:{billId:N}:{revisionId:N}` |
| Tally master id | The voucher inside Tally; the target of an alter | `tallyMasterId` in the `tally.posted` audit details |

`REMOTEID` rules:

- A create sends `REMOTEID = post:{billId:N}:{revisionId:N}`. It is the same for every attempt on one revision and changes when the bill is edited.
- `POST /api/bills/{billId}/repost` requires a caller-supplied, non-blank key of at most 128 characters and sends that exact trimmed value as `REMOTEID`. The posted bill goes straight to `posting`; there is no stored `posted → pending` step.
- An alter (a bill edited after Tally accepted it) sends no `REMOTEID`. It targets the voucher with `TAGNAME="MASTER ID"` and the master id from the pre-edit `tally.posted` audit event. If that id cannot be found the push fails with `TALLY_ALTER_TARGET_MISSING`; nothing is sent and no replacement voucher is created.
- The invoice number is not the Tally identity: a repost sends the same number again under a new key.

---

## 5. Replay / retry / ambiguity handling

### 5.1 Transport failures on voucher writes

Voucher writes are never automatically retried. Host-unreachable, timeout, connection-refused, HTTP-error, and unreadable-response cases may occur after Tally accepted the voucher, so `ITallyPoster` returns `Unknown` and the bill moves to `reconciliation_required`. The Polly retry pipeline applies only to safe Tally reads.

### 5.2 Definite failures

The bill moves to `failed` when Tally rejects the voucher outright:

- missing ledger
- missing stock item
- missing voucher type
- company mismatch
- period lock
- business validation rejection

### 5.3 Ambiguous outcomes

If the request may have reached Tally but success is uncertain:

1. do not blindly resend immediately
2. keep the bill in `reconciliation_required`, blocking normal mutation/retry/delete paths
3. verify the voucher in Tally
4. use admin **Mark as Pushed** if found, or **Mark as Pending** if absent

### 5.4 Checking Tally

Look the voucher up in Tally by invoice number, voucher type and company (the `REMOTEID` is in the `tally.outcome.unknown` audit details as `idempotencyKey`).

1. if found, mark the bill posted with an audit reason
2. if not found, mark the bill pending with an audit reason, then push again
