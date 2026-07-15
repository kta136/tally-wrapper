# Bill State Machine

This document freezes the current bill lifecycle from the existing system and the target lifecycle for Tally Wrapper.

Source priority for conflicts:

1. migration inventory / actual implemented behavior
2. final rebuild architecture recommendation
3. older requirement docs

---

## 1. V1 (Python) state model — historical, for migration context only

> Section 1 describes the **V1 Python system** that was the source of migration signal. Tally Wrapper does **not** use these states or transitions — see Section 3 for the actual Tally Wrapper state model. Keeping this section here documents what V1 did so future readers can understand the reasoning behind Tally Wrapper's choices.

### 1.1 V1 persisted states

| State | Meaning | Editable | Retryable | Repostable | Voidable | Deletable | Notes |
|---|---|---:|---:|---:|---:|---:|---|
| `draft` | Local unsynced draft | Yes | No | No | No | Yes | Can be requeued to `pending_sync` on save |
| `pending_sync` | Ready to push to Tally | Yes | No | No | Yes | Yes | Primary syncable state |
| `syncing` | In-flight Tally push | No | No | No | No | No | Interrupted-sync recovery support exists |
| `sync_failed` | Push failed | Yes | Yes | No | No | Yes | Retry and edit are both allowed |
| `sync_review` | Interrupted/manual review bucket | No normal edit | No | No | No | Yes | Recovery-oriented state |
| `void_pending` | Pending bill voided locally | No normal edit | No | No | Already voided | Yes | Local tombstone state |
| `posted` | Successfully pushed to Tally | Yes | No | Yes | No | No | Current UI allows reopening same bill for edit |

### 1.2 Current transitions actually implemented

| Event | From | To | Owner in current system | Source |
|---|---|---|---|---|
| `start_sync` | `pending_sync`, `sync_failed` | `syncing` | Sync controller/service | `services/bill_state_machine.py` |
| `sync_posted` | `syncing` | `posted` | Sync controller/service | `services/bill_state_machine.py` |
| `sync_failed` | `syncing` | `sync_failed` | Sync controller/service | `services/bill_state_machine.py` |
| `recover_interrupted` | `syncing` | `sync_review` | Recovery path | `services/bill_state_machine.py` |
| `void_pending` | `pending_sync` | `void_pending` | Bills controller/service | `services/bill_state_machine.py` |
| `requeue_after_edit` | `draft`, `pending_sync`, `sync_failed`, `posted` | `pending_sync` | Bill service update path | `services/bill_state_machine.py` |

### 1.3 Current editability rules

| Behavior | Current rule |
|---|---|
| Edit draft | Allowed |
| Edit pending_sync | Allowed |
| Edit sync_failed | Allowed |
| Edit posted | Allowed in current implementation |
| Edit sync_review | Not surfaced as normal operator path |
| Edit void_pending | Not normal path |

### 1.4 Current retry / repost / void / delete rules

| Action | Current rule |
|---|---|
| Retry failed bill | Only `sync_failed` |
| Post pending bill | Only `pending_sync` |
| Repost posted bill | Only `posted`, with explicit confirmation |
| Void pending bill | Only `pending_sync`, single-item action |
| Delete local bill | Allowed for `draft`, `pending_sync`, `sync_failed`, `sync_review`, `void_pending` |
| Batch actions | Only `retry`, `post`, `print`, `delete` have batch paths; all-or-nothing eligibility |

### 1.5 Current posted-bill behavior

This is migration-critical.

Current actual behavior:

- `Revise Posted` in the UI does **not** create a linked revision draft
- the normal UI path reopens the same posted bill for edit
- saving that edit mutates the same bill record
- the same bill is requeued back to `pending_sync`
- `posted_at`/push-related metadata remain semantically important
- `edited_after_push=True` tracks that the posted bill was changed after initial push

This current behavior wins over older requirement-doc wording.

---

## 2. Current behavior to preserve vs redesign

### 2.1 Preserve in Tally Wrapper

| Behavior | Preserve? | Why |
|---|---|---|
| Distinct retry vs repost actions | Yes | Operators currently treat them as different recovery actions |
| Pending-only void | Yes | Existing business rule |
| Batch vs single-item action split | Yes | Existing workflow behavior |
| Current state visibility in bills/history | Yes | Migration-critical operator expectation |
| Posted-bill behavior as a tracked migration issue | Yes | Must not be lost silently |

### 2.2 Intentionally redesign in Tally Wrapper

| Behavior | Redesign? | Target direction |
|---|---|---|
| Same-bill mutation after posting | Yes | Move to immutable finalized snapshots once numbered/pushed |
| `Revise Posted` label/behavior mismatch | Yes | Make workflow explicit and consistent |
| Local sync states tied to desktop-owned posting | Yes | Move posting orchestration to the cloud API (in-process Tally calls) |
| Local lock semantics | Yes | Replace with server-owned leases where needed |

---

## 3. Target Tally Wrapper state model

### 3.1 Target states

| State | Meaning |
|---|---|
| `pending` | Mutable saved bill not yet posted to Tally |
| `posting` | API is mid-call to Tally right now (typically seconds; briefly visible while the HTTP post is in flight) |
| `posted` | Tally accepted the voucher |
| `failed` | Latest post attempt failed; operator must click Retry |
| `reconciliation_required` | The write may have reached Tally, but the API cannot prove success or failure; an admin must verify Tally and explicitly mark the bill posted or pending |
| `revised` | Old pending lineage superseded by a newer pending revision |
| `voided` | Bill cancelled/closed |

No queue state. Posting to Tally is synchronous and operator-initiated — the bill briefly occupies `posting` only while the API's HTTP call to Tally is outstanding.

### 3.2 Target transitions

| Event | From | To | Notes |
|---|---|---|---|
| Save bill | new / `revised` lineage | `pending` | Mutable working state; invoice number reserved at creation |
| **Manual push** (operator click) | `pending`, `draft`, `failed` | `posting` → `posted` \| `failed` \| `reconciliation_required` | API calls Tally XML synchronously via `ITallyPoster`; first pushes create a voucher, edited-after-push bills alter the prior Tally voucher. Transport timeouts/unreadable responses are ambiguous, not ordinary failures. |
| Manual retry | `failed` | `posting` → `posted` \| `failed` \| `reconciliation_required` | Same sync path as push. |
| Manual repost | `posted`, `failed` | `posting` → `posted` \| `failed` \| `reconciliation_required` | Same sync path; the caller-supplied repost idempotency key becomes the Tally `REMOTEID` for create/import. |
| Revise bill content | `pending` only in normal flow | `revised` + new `pending` | Old pending bill becomes superseded |
| **Edit in place** (admin-gated) | `pending`, `draft`, `failed`, `posted` | `pending` (with `EditedAfterPush=true` if reopened from non-pending) | Appends revision; invoice number unchanged. Blocked on `posting`. |
| **Mark as Pushed** (admin-gated) | `pending`, `draft`, `failed`, `reconciliation_required` | `posted` | Local-only attestation — does not call Tally. This is one of the two explicit reconciliation exits. Requires reason ≥ 4 chars. |
| **Mark as Pending** (admin-gated) | `posted`, `failed`, `reconciliation_required` | `pending` | Local-only revert — does not touch Tally. This is the other explicit reconciliation exit. Clears `EditedAfterPush`. |
| **Change bill number** (admin-gated) | any except `posting`, `reconciliation_required` | (same state) | Updates `bill.InvoiceNumber`. Warnings: `LeavesGap`, `TallyDiverges`, `ReservationOrphaned`. 409 on uniqueness conflict. |
| Void before posting | `pending`, `draft`, `failed` | `voided` | |
| **Delete local** (admin-gated, hard-delete) | any except `posting`, `reconciliation_required` | (row removed) | Cascades bill_revisions. Keeps the append-only audit history and a final `bill.deleted` tombstone. |

### 3.3 Target editability rules

| State | Editable in Tally Wrapper? | Rule |
|---|---:|---|
| `pending` | Yes | Normal editing state |
| `posting` | No | Active Tally call — never editable |
| `reconciliation_required` | No | Verify the voucher in Tally, then use admin Mark as Pushed or Mark as Pending |
| `posted` | **Admin-only reopen** | Reopens to `pending`; sets `EditedAfterPush=true` |
| `failed` | **Admin-only reopen** | Same as posted reopen |
| `revised` | No | Historical superseded record |
| `voided` | No | Terminal |

### 3.4 Posting mechanics

There is no `tally_posting_jobs` table or queue. On push/retry/repost, `BillService.PushInternalAsync`:

1. Transitions the bill to `posting` and saves.
2. If `EditedAfterPush=true`, resolves the previous Tally `MASTER ID` from the pre-edit `tally.posted` audit. If it cannot, the push fails with `TALLY_ALTER_TARGET_MISSING` without creating a fallback voucher.
3. Calls `ITallyPoster.PostAsync` synchronously (builds create XML, or alter XML for edited-after-push, sends via HTTP to Tally, parses response).
4. Writes `tally.posted`, `tally.failed`, or `tally.outcome.unknown` and transitions the bill to `posted`, `failed`, or `reconciliation_required`. The audit trail is append-only, including across edit-and-repush flows.

If the API crashes mid-post, `StuckPostingRecoveryHostedService` moves any stranded `posting` bill to `reconciliation_required` with a `bill.posting.recovered` audit event. It never makes a potentially duplicate write safe merely by restarting the API.

---

## 4. Tally Wrapper layer ownership

Everything Tally-related runs in-process in the API. There is no separate bridge; Tally calls happen inside the HTTP request that triggered them.

| Transition / action | Desktop | API |
|---|---:|---:|
| Save bill | Owns input and command | Persists pending bill |
| Push bill | Initiates | Owns validation/finalization and the synchronous Tally post |
| Allocate number | No | Owns |
| Post XML to Tally | No | Owns (inside `ITallyPoster` during the push request) |
| Classify posting result | No | Owns final state update from Tally's response |
| Retry / repost | Initiates | Owns state change + re-post |
| Revision workflow | Initiates | Owns persistence/state |
| Void workflow | Initiates | Owns persistence/state |

---

## 5. Current behavior to preserve vs redesign

### Preserve

- explicit retry vs repost distinction
- explicit pending/failed/posted state visibility in operator UI
- batch action differences
- current operator expectation that posted bills need special handling
- current migration-critical knowledge that older docs do not match real behavior

### Redesign

- do not carry forward the current misleading `Revise Posted` label while changing semantics silently
- do not let numbered/finalized bills remain casually mutable in Tally Wrapper
- do not let desktop clients directly own posting lifecycle state
- do not rebuild the old local-state machine around local durable storage

### Decision note

Tally Wrapper intentionally redesigns the posted-bill correction model. The new target is immutable finalized snapshots with explicit repost or revision workflows. That is a deliberate redesign, not an accidental drift.
