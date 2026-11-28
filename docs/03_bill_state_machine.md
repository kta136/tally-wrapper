# Bill State Machine

New bills enter `pending` when saved. The API owns state changes and records them in the bill audit trail. Printing a bill leaves its state unchanged.

## Bill states

| State | Meaning |
|---|---|
| `pending` | Saved bill awaiting a push or an update to an existing voucher |
| `draft` | Treated like `pending` wherever it is found; a new bill is saved as `pending` |
| `posting` | API is mid-call to Tally right now (typically seconds; briefly visible while the HTTP post is in flight) |
| `posted` | Tally accepted the voucher or an admin verified it and marked the bill as pushed |
| `failed` | Latest attempt failed with a known outcome. The operator can correct the issue and retry. |
| `reconciliation_required` | The write may have reached Tally, but the API cannot prove success or failure; an admin must verify Tally and explicitly mark the bill posted or pending |
| `revised` | Old pending lineage superseded by a newer pending revision |
| `voided` | Bill cancelled/closed |

## Actions and transitions

| Event | From | To | Notes |
|---|---|---|---|
| Create bill | new bill | `pending` | The API reserves the invoice number when it creates the bill |
| **Manual push** (operator click) | `pending`, `draft`, `failed` | `posting` → `posted` \| `failed` \| `reconciliation_required` | API calls Tally XML synchronously via `ITallyPoster`; first pushes create a voucher, edited-after-push bills alter the prior Tally voucher. Transport timeouts/unreadable responses are ambiguous, not ordinary failures. |
| Manual retry | `failed` | `posting` → `posted` \| `failed` \| `reconciliation_required` | Same sync path as push. |
| Manual repost | `posted`, `failed` | `posting` → `posted` \| `failed` \| `reconciliation_required` | Same sync path; the caller-supplied repost idempotency key becomes the Tally `REMOTEID` for create/import. |
| Revise bill content | `pending`, `draft` | `revised` + new `pending` | The old bill becomes superseded; the new bill gets its own invoice number |
| **Edit in place** | `pending`, `draft`, `failed`; `posted` with an admin session | Current state for an existing `pending` or legacy `draft`, otherwise `pending` | Appends a revision and keeps the invoice number. `EditedAfterPush=true` only when Tally holds the bill's voucher, so the next push alters it; a bill whose first push was rejected is pushed again as a create. Without an admin session an edit to a `posted` bill is `403 Admin session required`. Refused in `posting`, `reconciliation_required`, `voided` and `revised`. |
| **Mark as Pushed** (admin-gated) | `pending`, `draft`, `failed`, `reconciliation_required` | `posted` | Local-only attestation — does not call Tally. This is one of the two explicit reconciliation exits. The Desktop asks for a reason of at least 4 characters. |
| **Mark as Pending** (admin-gated) | `posted`, `failed`, `reconciliation_required` | `pending` | Local-only revert — does not touch Tally. This is the other explicit reconciliation exit. Clears `EditedAfterPush`. |
| **Change bill number** (admin-gated) | any except `posting`, `reconciliation_required` | same state; a bill whose voucher Tally holds becomes `pending` with `EditedAfterPush=true` | Updates `bill.InvoiceNumber` and `InvoiceCore`. Warnings: `LeavesGap`, `TallyDiverges`. 409 on uniqueness conflict. |
| Void before posting | `pending`, `draft`, `failed` | `voided` | Refused while Tally holds the bill's voucher. The Desktop asks for confirmation first. |
| **Delete local** (admin-gated, hard-delete) | any except `posting`, `reconciliation_required` | (row removed) | Cascades bill_revisions. Keeps the append-only audit history and a final `bill.deleted` tombstone. |

## Posting and recovery

1. Preflight checks the connection and company. For an edited bill whose voucher Tally holds, the API first resolves the voucher using its audited master ID and current Tally date. Where a manual mark left no ID, it checks the number and verifies the candidate against the recorded bill. An ambiguous or missing target stops the attempt.
2. The API claims the bill with a conditional update to `posting`. Only the request that acquired the claim may send the voucher.
3. `ITallyPoster.PostAsync` makes one voucher write. The API records `tally.posted`, `tally.failed` or `tally.outcome.unknown` and sets the corresponding state. After acceptance, a read-back can record Tally's filed voucher number in `tally.voucher.filed`.

An edit that loses a concurrency race returns HTTP 409. Reload the saved bill before reapplying the edit.

If the API crashes mid-post, or cannot reach the database to record the outcome once Tally has answered, the bill is left in `posting`. `StuckPostingRecoveryHostedService`, which sweeps at API start and every 5 minutes after, moves any stranded `posting` bill to `reconciliation_required` with a `bill.posting.recovered` audit event. Because another API process on the same database may be mid-push, a `posting` claim is only treated as abandoned once it is older than 10 minutes (longer than the 300 s Tally budget plus settling); younger claims are re-checked later, conditionally on the claim being unchanged. It never makes a potentially duplicate write safe merely by restarting the API.

See the [Tally integration contract](05_tally_integration_contract.md) for preflight, result classification and reconciliation.
