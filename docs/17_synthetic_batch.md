# Synthetic Batch Data Scheduler

Ported from V1 (`Tally_Wrapper/services/synthetic_bill_generator.py` +
`Tally_Wrapper/ui/synthetic_bill_scheduler_dialog.py`). Generates a cluster of
realistic `pending` bills with backfilled audit timestamps, for QA / volume
padding / business-day replay. Available in production, admin-gated.

## Surface

- **API:** `POST /api/bills/synthetic-batch` — `[Authorize(Policy = "Admin")]`, takes
  [`SyntheticBatchRequest`](../src/ShowroomBilling.Contracts/Bills/SyntheticBatchContracts.cs)
  and returns `SyntheticBatchResponse`.
- **Desktop:** Settings → Admin → **Open Batch Data Scheduler…** opens the
  [`SyntheticBatchDialog`](../src/ShowroomBilling.Desktop/Views/SyntheticBatch/SyntheticBatchDialog.xaml).
  The Admin section is shown only while admin is unlocked. If the session has
  expired when **Start Schedule** is pressed, the unlock dialog opens first.
- **Key:** `Ctrl+Alt+G` opens the same dialog from any screen, as in V1, but
  only while admin is unlocked and no other dialog is open. While admin is
  locked the key does nothing, and it is not in the shortcuts list.

## Input model (V1-parity)

| Field | Units | Rule |
|---|---|---|
| `TotalAmount` | ₹ | partitioned into bills; at least `₹50,000`, the smallest bill |
| `MaxBillAmount` | ₹ | `₹50,000` to `₹1,99,000` (the top is a compliance cap; cannot be relaxed) |
| `Rate24Kt` | ₹/g | > 0. The dialog has no built-in rate: it starts from the Invoice tab's 24kt rate when one is set, otherwise the operator types it. |
| `PaymentMode` | string | `Cash`, or anything else → "Credit and debit" (`PaymentMode.Normalize`). It is the bill's payment, which picks the voucher's party ledger. Until 20 November 2026 it was also the bill's party; bills made before then keep "Cash" or "Credit and debit" there (before 10 October 2026, "Credit and Debit" with a capital D). |
| (party) | | Not an input. Each bill is made out to a random Indian customer, a first name and a surname from one region's names (`SyntheticCustomerNames`), men and women alike. The party goes into the Tally narration, so vouchers read `Rahul Sharma \| Cash`, and is the buyer's mailing name on the voucher; it is never a ledger. |
| `MinItemsPerBill` / `MaxItemsPerBill` | int | `1..25`, min ≤ max |
| `StartAtUtc` / `EndAtUtc` | UTC | From the last stored bill to now (owner, 20 November 2026): start no earlier than the earliest time (below), end no later than now, start < end; the window must have ≥ bill-count minute slots from the earliest time on. The earliest time is the minute after the last bill was saved, except that a bill saved on a later day than its date counts as the end of that date (owner, 21 November 2026: a bill saved today but dated yesterday lets a batch start at 00:00 today), and one dated ahead of the day it was saved counts from the start of its date. `GET /api/bills/synthetic-batch/window` gives it (`SyntheticBatchExecutor.EarliestBillTimeAsync`). The dialog fills in that time and now when it opens (and again after a batch), and refuses a time outside them. With no bill yet, it starts at the start of today. The server also takes a start in the minute before the earliest time, which is what a counter built before 21 November 2026 offers (the last bill's own time); no bill goes in that minute. |
| `SelectedKaratLabels` | list | must be non-empty subset of Tally-mapped karats |

## Algorithm

1. `PartitionBillTotals` — partitions `TotalAmount` into random per-bill totals
   drawn uniformly from `[₹50,000, MaxBillAmount]`. Any remainder under
   `₹50,000` is dropped (as in V1). So a batch can come to as much as
   ₹49,999 less than `TotalAmount`, and can hold one bill fewer than
   `TotalAmount ÷ MaxBillAmount` rounded up: ₹4,00,000 at ₹1,99,000 a bill is
   two bills in about 1 batch in 19, not always three or more.
2. `BuildRandomSchedule` — samples `N` distinct minute-slots from the interval
   `[ceil_minute(Start), floor_minute(End)]` without replacement, sorted
   ascending, none before the earliest time (see `StartAtUtc` above), so the
   batch's bills follow every stored bill in date as they do in number. It
   refuses a window that starts before that time or ends after now, and says
   so when the earliest time is itself later than now (a batch made before
   20 November 2026 could date bills later in the day than it was run). A
   batch bill can be timed before a bill saved the same day but dated the day
   before, and is numbered after it: Tally sees only the dates, which stay in
   number order.
3. **Items.** Diamond jewellery and stones are never billed (`MayBill`): an
   item of the Diamond category, one not sold by the gram (a stone sold by the
   carat, `ct`; a unit of `g`, `gm`, `gms`, `gram` or `grams` in any case, or
   none, is grams), and one whose name contains "diamond" or "stone" (so a gold-based "Diamond Earring" or
   "Stone Ring" too). A batch whose item masters are all of that kind is
   refused. (v1.4 took only `gm` or no unit as grams, so an item list saved
   by an older build, whose gold items say `g`, was refused outright.) Each remaining item × selected (Tally-mapped) karat is priced per
   gram by [`BillCalculator`](../src/ShowroomBilling.Application/Bills/BillCalculator.cs).
4. `BuildLineTargets` — splits each bill's total into
   `MinItemsPerBill`..`MaxItemsPerBill` shares, each enough for 1 g of the
   dearest item, as many as fit (a ₹50,000 bill at ₹13,000 a gram has at most
   three lines). Each share gets that minimum and the rest is cut at random
   points among them.
5. **Weights.** Each line gets a random item × karat and weighs what its share
   buys, rounded down to `0.01 g`, never under `1 g`. Steps of `0.01 g` are then
   added to lines picked at random while the bill stays within its total, so a
   bill comes to its total or up to one step (₹100–150) under it. A total within
   a step of ₹50,000 can still be under it; then the cheapest line takes steps
   until it is not. If that puts the bill over `MaxBillAmount` (only possible
   when it is within about two steps of ₹50,000) the bill is priced again with
   other items, up to 20 times, before the batch is refused.
6. `SyntheticBatchExecutor` — loops planned bills, calls
   [`BillService.CreateBackdatedDraftAsync`](../src/ShowroomBilling.Infrastructure/Bills/BillService.cs)
   with the planned `ScheduledAtUtc`. Each bill takes the next invoice number
   from `NumberingService` exactly as a saved bill does (numbers follow the
   order the bills are created in, within the fiscal year of the bill date),
   then persists bill + revision + the `bill.pending.created` audit event, all
   stamped with the backfilled time.

## Invariants

- **Bills land in `pending`.** Identical to a user-saved draft; see
  [`docs/03_bill_state_machine.md`](03_bill_state_machine.md).
- **No Tally traffic.** The executor never calls `ITallyPoster`; the only
  write path is `CreateBackdatedDraftAsync`.
- **Every bill's grand total, with GST, is from ₹50,000 to `MaxBillAmount`.**
  Until 20 November 2026 a line was priced at a random 35–90% of its share, so
  bills came to well under the range shown (some about ₹9,000) and a batch to
  about 62% of `TotalAmount`. Now the bills add up to close to `TotalAmount`
  (step 1 and step 5 say how close).
- **No line under 1 g, and every weight in steps of 0.01 g.** Before 20 November
  2026 a line could weigh 0.001 g, and a diamond item, which had no rate there,
  was replaced by a line of 0.001 g.
- **A batch's bills follow every stored bill in date as in number.** Each is
  saved only if no stored bill comes after it by the same rule as the earliest
  time: none of a later date, and none of its own date (or with no date) made
  in its minute or after; a bill of an earlier date never does. It is checked
  while the lock on
  the year's numbering row is held (`BillLifecycleWorkflow.CreateNewDraftAsync`,
  backdated bills only). So a bill a counter saves while a batch is being saved
  stops the batch there with a message saying how many were saved, instead of
  being numbered before batch bills dated earlier than it. The bills saved
  before it stay.
- **No two bills share a minute-slot.** Enforced by
  `SampleWithoutReplacement` in `BuildRandomSchedule` and verified by the
  `BuildRandomSchedule_ProducesSortedDistinctMinuteSlots` test.
- **Audit events carry the backfilled timestamp.** Verified by
  `CreateBackdatedDraftAsync_UsesOverrideForBillAndAudit`.

## Constants (do not relax without product sign-off)

| Symbol | Value | Source |
|---|---|---|
| `MinBillTotal` | `₹50,000` | owner, 20 November 2026 (V1: `₹25,000`, `synthetic_bill_generator.py:15`) |
| `HardMaxBillAmount` | `₹1,99,000` | V1 `synthetic_bill_generator.py:143` |
| `MinLineWeight` | `1 g` | owner, 20 November 2026 (V1: `0.001 g`, `synthetic_bill_generator.py:17`) |
| `WeightStep` | `0.01 g` | owner, 20 November 2026 (V1: `0.001 g`) |
| `MaxItemsPerBillCap` | `25` | V1 `synthetic_bill_generator.py:115` (UI spin max) |

V1's `₹500` smallest line share, its `uniform(0.35, 0.9)` weight fraction and
its 8-attempt line split went with the old weights.

## Running it

### API smoke

Run this against a scratch database only. A plain `dotnet run` uses whatever
database connection this PC has saved; see
[DEV_SETUP.md](../DEV_SETUP.md#run-against-a-scratch-database).

The window in step 3 must start no earlier than the earliest time
(`GET /api/bills/synthetic-batch/window`) and end no later than now; change
the two times to fit.

```powershell
# 1. Start the API
dotnet run --project src/ShowroomBilling.Api

# 2. Unlock admin
curl -X POST http://localhost:5108/api/admin/unlock \
  -H "Content-Type: application/json" \
  -d '{"passcode":"<your-passcode>","actorLabel":"dev"}'
# → { "token": "...", ... }

# 3. Post a batch
curl -X POST http://localhost:5108/api/bills/synthetic-batch \
  -H "Content-Type: application/json" \
  -H "X-Admin-Token: <token>" \
  -d '{
    "totalAmount": 500000,
    "maxBillAmount": 199000,
    "rate24Kt": 7200,
    "paymentMode": "Cash",
    "minItemsPerBill": 1,
    "maxItemsPerBill": 3,
    "startAtUtc": "2026-04-24T03:30:00Z",
    "endAtUtc": "2026-04-24T18:30:00Z",
    "selectedKaratLabels": ["22K", "18K"]
  }'
```

### Desktop smoke

1. Launch the Desktop on a scratch database (`.\tools\run-scratch.ps1`).
2. Press `~` to unlock admin (a new scratch database asks for a passcode to be set first).
3. Navigate **Settings → Admin → Open Batch Data Scheduler…**, or press `Ctrl+Alt+G`.
4. Fill the form and click **Start Schedule**. The times start at the last
   bill and end now; on a new scratch database, at the start of today.
5. The **Bills** tab refreshes automatically on completion; new bills appear
   with `Updated` timestamps inside the selected window, in state `pending`,
   each made out to a customer's name.
