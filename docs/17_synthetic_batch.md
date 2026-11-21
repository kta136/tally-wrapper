# Synthetic Batch Data Scheduler

Ported from V1 (`Tally_Wrapper/services/synthetic_bill_generator.py` +
`Tally_Wrapper/ui/synthetic_bill_scheduler_dialog.py`). Generates a cluster of
realistic `pending` bills with backfilled audit timestamps, for QA / volume
padding / business-day replay. Available in production, admin-gated.

## Surface

- **API:** `POST /api/bills/synthetic-batch` — `[Authorize(Policy = "Admin")]`, takes
  [`SyntheticBatchRequest`](../src/ShowroomBilling.Contracts/Bills/SyntheticBatchContracts.cs)
  and returns `SyntheticBatchResponse`: the bills made, what they come to, and
  the first and last bill number. `GET /api/bills/synthetic-batch/{batchId}`
  says how far a batch got (see [A batch that stops part-way](#a-batch-that-stops-part-way)).
  `GET /api/bills/synthetic-batch/window` gives the form the earliest time a
  batch may be dated from and the shop hours it keeps to;
  `GET` / `PUT /api/settings/batch-hours` read and save those hours (see
  [Shop hours](#shop-hours)).
- **Desktop:** Settings → Admin → **Open Batch Data Scheduler…** opens the
  [`SyntheticBatchDialog`](../src/ShowroomBilling.Desktop/Views/SyntheticBatch/SyntheticBatchDialog.xaml).
  The Admin section is shown only while admin is unlocked. If the session has
  expired when a batch is started, the unlock dialog opens first. See
  [The dialog](#the-dialog).
- **Key:** `Ctrl+Alt+G` opens the same dialog from any screen, as in V1, but
  only while admin is unlocked and no other dialog is open. While admin is
  locked the key does nothing, and it is not in the shortcuts list.

## The dialog

One screen, two columns, laid out on 21 November 2026 from a design the owner
approved. (Before that it was one tall column whose result line was below the
fold, and Start ran at once.)

- **Left, the form:** Amount (total, largest single bill, 24K rate, payment,
  items per bill), Dates (a date picker and a typed time of day for From and
  for To, and **Last bill to now**, which puts both back), and the karats. A
  time is typed as hours and minutes on the 24-hour clock; `10:43`, `9:05`,
  `1043` and `10` are all read (`BatchClock`), and text that is no time is put
  back as it was. What stops a batch is said in one banner across the form.
- **Right, "This batch makes":** how many bills the form will make, as a range
  (`SyntheticBillPlanner.EstimateBillCountBounds`), what they add up to, the
  times, and the shop hours, closed day and gap a batch keeps to. When the
  times are too short to keep the gap, a quiet line says how many bills they
  have room for; it does not stop the batch.
- **"Make bills…" asks first.** The question lists the total, the rate, the
  times, the shop hours, the payment, the items per bill and the karats. A
  rate more than a tenth away from the Invoice tab's turns it into a warning
  with Cancel as the focused button: a missing or an extra digit prices every
  bill of the batch wrongly. The button is not on the Enter key, because a
  time just typed is taken only when the cursor leaves its box.
- **While a batch runs** the form stays, kept from keys and clicks, and the
  right side shows that the server is working (it makes a batch in one call,
  so the bar cannot show how far). The dialog can be closed meanwhile; the
  batch goes on and the dialog shows it when opened again. If admin had to be
  unlocked first, the dialog comes back in front once the unlock dialog closes.
- **When a run ends, its result takes the form's place** (`BatchResult`): how
  many bills were made, what they come to and the first and last bill number;
  or "Stopped after 3 of 12 bills" with the reason; or that nothing was saved;
  or that the count is not known. "What was asked" stays beside it. **Open
  Bills tab** closes the dialog and shows the Bills tab with its filters
  cleared, so that a filter left on does not hide the new bills; **Make
  another batch** brings the form back. A result stays until the operator
  sends it away with one of those two: closing the dialog and opening it
  again shows it still, so a result that arrived while the dialog was closed
  is not lost.
- **The batch sent is the one that was approved.** The question, the request
  and "What was asked" are all taken from the form at the moment "Make
  bills…" is pressed; a form that changes while the question is open (the
  read of the last bill's time may still be on its way) changes none of them.
  The shop hours shown go with the request (`ExpectedHours`), and the server
  refuses the batch when they are no longer the saved ones, so no bill is
  dated by hours the operator did not see.
- **"Adds up to" says "about", and is a range when the limits can keep a
  batch under its total.** Each bill lands a weight step or less under its
  share, and a batch holds at most its bills at the largest bill each:
  ₹4,00,000 at ₹1,99,000 a bill shows "About ₹3,98,000 to ₹4,00,000", and
  ₹99,999 at ₹60,000 a bill, which can only be one bill, "About ₹60,000".
- **Shop hours are saved only after they have been read.** The card in
  Settings → Admin takes no keys while it reads or saves, and offers **Read
  again** in place of saving when a read failed: boxes that are empty because
  the read failed would otherwise be saved over the hours of every counter.
  A save that gets no answer is read back and shown, not called a failure.

`DesignDialogTests` keeps pictures of the form, a blocked form, three results
and the question in snapshot mode (`Design-Dialog-SyntheticBatch*.png`,
`Design-Dialog-Confirm-Batch.png`).

## Input model (V1-parity)

| Field | Units | Rule |
|---|---|---|
| `TotalAmount` | ₹ | partitioned into bills; at least `₹50,000`, the smallest bill |
| `MaxBillAmount` | ₹ | `₹50,000` to `₹1,99,000` (the top is a compliance cap; cannot be relaxed) |
| `Rate24Kt` | ₹/g | > 0. The dialog has no built-in rate: it starts from the Invoice tab's 24kt rate when one is set, otherwise the operator types it. |
| `PaymentMode` | string | `Cash`, or anything else → "Credit and debit" (`PaymentMode.Normalize`). It is the bill's payment, which picks the voucher's party ledger, and the bill's note, so the Tally narration reads `Rahul Sharma \| Credit and debit` on a card bill (owner, 21 November 2026; before that every batch bill's note was "Cash", as in V1). Until 20 November 2026 it was also the bill's party; bills made before then keep "Cash" or "Credit and debit" there (before 10 October 2026, "Credit and Debit" with a capital D). |
| (party) | | Not an input. Each bill is made out to a random Indian customer, a first name and a surname from one region's names (`SyntheticCustomerNames`), men and women alike. The party goes into the Tally narration, so vouchers read `Rahul Sharma \| Cash`, and is the buyer's mailing name on the voucher; it is never a ledger. |
| `MinItemsPerBill` / `MaxItemsPerBill` | int | `1..25`, min ≤ max |
| `StartAtUtc` / `EndAtUtc` | UTC | From the last stored bill to now (owner, 20 November 2026): start no earlier than the earliest time (below), end no later than now, start < end; the window must have ≥ bill-count minute slots from the earliest time on, counting only the minutes inside the shop hours when hours are set (below). The earliest time is the minute after the last bill was saved, except that a bill saved on a later day than its date counts as the end of that date (owner, 21 November 2026: a bill saved today but dated yesterday lets a batch start at 00:00 today), and one dated ahead of the day it was saved counts from the start of its date. `GET /api/bills/synthetic-batch/window` gives it (`SyntheticBatchExecutor.EarliestBillTimeAsync`). The dialog fills in that time and now when it opens (and again after a batch), and refuses a time outside them. With no bill yet, it starts at the start of today. The server also takes a start in the minute before the earliest time, which is what a counter built before 21 November 2026 offers (the last bill's own time); no bill goes in that minute. |
| `SelectedKaratLabels` | list | The karats a batch may use, of those mapped to a Tally stock item. A label that is not mapped is left out without a word, and the batch is refused only when none of the labels is mapped. An empty list means every mapped karat (the dialog never sends one: it needs a karat ticked). |
| `ExpectedHours` | shop hours, optional | The shop hours the form showed. When they differ from the saved ones the batch is refused (400) and nothing is made. |
| `BatchId` | GUID, optional | An id the Desktop makes up for the batch, to ask about it afterwards. The same id sent twice makes no second batch (409), but only while the server still holds that batch (the last 20, and none after a restart): "not found" is not leave to send a batch again. What stops a second batch then is the window, which has to start after the bills the first one saved. |
| (shop hours) | India's time | Not sent with a batch: saved once in Settings → Admin and read by the server for every batch (owner, 21 November 2026). An opening and a closing time, a weekly closed day, and the fewest minutes between two bills. See [Shop hours](#shop-hours). |

### Shop hours

Owner, 21 November 2026. A batch dates its bills only while the shop is open,
so that they read like a day at the counter. The hours are
[`BatchHoursSettings`](../src/ShowroomBilling.Contracts/Bills/SyntheticBatchContracts.cs),
all in India's time whatever time zone the server or a counter is set to:

| Field | Rule |
|---|---|
| `Opens` / `Closes` | A bill is dated from the opening minute to the closing minute, both included. Given together or not at all; closing is later than opening on the same day (no hours that run past midnight); hours and minutes only, seconds are dropped. Neither set: any time of day. |
| `ClosedOn` | The weekday the shop is shut every week; no bill is dated on it. Not set: open every day. |
| `GapMinutes` | The fewest minutes between two bills of one batch, `0..120` (`BatchHoursLimits.MaxGapMinutes`); 0 or 1 is no gap. |

- **They are saved in Settings → Admin, not typed for each batch.** They are
  shared settings, kept with the others in `cloud_settings."BatchHoursJson"`
  ([10 §1.7](10_database_schema.md)): `GET /api/settings/batch-hours` reads
  them (no token), `PUT` saves them (admin only) and writes the audit event
  `settings.batch_hours_updated`. Until some are saved there are none: a batch
  may be dated at any minute of any day, as before.
- **The closed day is weekly.** There is no list of holidays.
- **The gap is kept when there is room, and otherwise the batch is still
  made**, with its bills closer together: the largest gap that fits the open
  minutes, down to a bill a minute. The dialog says so before the batch is
  sent; it does not block. Only too few open minutes for the bills, one minute
  each, refuses a batch.
- **One class counts the open minutes**,
  [`BatchOpenMinutes`](../src/ShowroomBilling.Application/Bills/BatchOpenMinutes.cs),
  for the planner and for the dialog alike: `Count` is how many minutes of a
  window are open, `RoomAtGap` how many bills fit that many minutes at a gap.
  The window endpoint hands the hours to the dialog so that it can say how
  many bills its times have room for.

## Algorithm

1. `PartitionBillTotals` — partitions `TotalAmount` into random per-bill totals
   drawn uniformly from `[₹50,000, MaxBillAmount]`. What is left under
   `₹50,000` after the last bill is no bill of its own; it goes to the bills
   that have room under `MaxBillAmount`, a random part each in a random order
   (owner, 21 November 2026; V1 dropped it, so a batch came to as much as
   ₹49,999 less than `TotalAmount`). Only what no bill has room for is not
   billed, which takes every bill being at the max: a batch can hold one bill
   fewer than `TotalAmount ÷ MaxBillAmount` rounded up, so ₹4,00,000 at
   ₹1,99,000 a bill is two bills in about 1 batch in 19, and those two then
   come to ₹3,98,000.
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

   With shop hours set (owner, 21 November 2026) the slots are the open minutes
   only: those whose time of day in India is from the opening to the closing
   time and whose weekday there is not the closed day. They are worked out a
   day at a time and laid end to end as one line. A window with fewer open
   minutes than bills is refused; when none of its minutes is open the refusal
   says so and names the hours, the closed day and where they are set. The
   bills are then placed on that line at least `GapMinutes` apart when it has
   room for that (`(open − 1) ÷ gap + 1` bills or more), and otherwise at the
   largest gap that fits, `(open − 1) ÷ (bills − 1)`, never under one minute.
   Every placement that keeps the gap is as likely as any other: `N` distinct
   positions are drawn on a line shortened by `(N − 1) × (gap − 1)`, and
   `gap − 1` minutes are put back after each bill. Because the gap is measured
   along the open minutes, a night or a closed day between two bills only
   puts them further apart. With no hours and no gap the draw is exactly what
   it was.
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
   stamped with the backfilled time. Each line carries the item's pricing mode
   and category, as a line saved in the Invoice tab does, so a batch bill
   opened for edit is priced by the mode it was made with and not by the item
   master as it is by then (before 21 November 2026 it carried neither).

## A batch that stops part-way

Each bill is its own transaction, so a batch that stops keeps the bills it
has saved. It stops when a counter saves a bill meanwhile (409, see
Invariants), when the run is cancelled or the Desktop's request gives up
(10 minutes; the usual 100 seconds cut a large batch off), when the
connection drops, and when a save fails.

- **The server stops between two bills.** A cancelled request does not cut
  off the bill in hand: it is saved whole and the batch stops before the next.
- **`SyntheticBatchOutcomes` keeps how far each batch got**, by the `BatchId`
  its Desktop sent: bills planned, bills saved, their total, the first and
  last number, and whether the batch has finished. It is in memory only, the
  last 20 batches, and is gone when the API restarts; nothing is stored with
  the bills, which carry no mark of a batch.
  `GET /api/bills/synthetic-batch/{batchId}` returns it (404 when unknown).
- **The dialog asks it whenever its request did not end with the batch's
  answer**, and says "Stopped after 3 of 12 bill(s)" with the reason, loads
  the Bills tab again and moves its window after the bills now stored. It
  stays "running", with Start off, until the server says the batch has
  finished (up to the 10 minutes a batch may take): the bill in hand can take
  a while on a stalled line. When every bill was saved and only the answer
  was lost, it reports the batch as made. When the server holds nothing or
  cannot be reached, it says the count is not known and to look in the Bills
  tab. Only a refusal made before a batch is planned (400, 401, 403) leaves
  the Bills tab and the times alone. (Before 21 November 2026 a
  stopped batch said only "cancelled" or "failed", the Bills tab was not
  loaded again, and the next Start was refused because its start time was by
  then before the bills the batch had saved.)
- **A save that does not say whether its bill was stored leaves the count
  in doubt.** When saving a bill fails in any way other than a refusal made
  before anything is written (the database stops answering at the commit),
  that bill may be stored and not counted. The outcome carries
  `lastSaveInDoubt`, and the dialog says that one more bill may have been
  saved and to look in the Bills tab, also when it was the first bill.
- **A refusal can come after bills were saved** (a bill that fails its checks
  part-way is a 400), so the dialog asks for the outcome whatever the answer
  was, and takes "nothing was saved" only from a refusal that left no record
  under the batch's id.

## Invariants

- **Bills land in `pending`.** Identical to a user-saved draft; see
  [`docs/03_bill_state_machine.md`](03_bill_state_machine.md).
- **No Tally traffic.** The executor never calls `ITallyPoster`; the only
  write path is `CreateBackdatedDraftAsync`.
- **Every bill's grand total, with GST, is from ₹50,000 to `MaxBillAmount`.**
  Until 20 November 2026 a line was priced at a random 35–90% of its share, so
  bills came to well under the range shown (some about ₹9,000) and a batch to
  about 62% of `TotalAmount`. Now the bills add up to `TotalAmount` less a
  weight step or less a bill (step 1 says when the bills cannot hold it all).
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
- **No bill is dated outside the shop hours or on the closed day** (owner,
  21 November 2026), read in India's time, once hours are saved in Settings →
  Admin. The gap between two bills is kept whenever the open minutes have room
  for it; when they do not, the batch is made with the bills closer together
  and nothing else gives way: the hours, the closed day and one bill a minute
  still hold. The planner reads the hours the server has stored, never hours
  sent with a batch. Verified by the `BuildRandomSchedule_WithShopHours_…` and
  `BuildRandomSchedule_…Gap…` tests and, against the stored hours,
  `A_batch_keeps_to_the_shop_hours_that_were_saved`.
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
| `BatchHoursLimits.MaxGapMinutes` | `120` | owner, 21 November 2026 (the largest gap that can be saved) |

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
4. Fill the form, click **Make bills…** and say yes to the question. The times
   start at the last bill and end now; on a new scratch database, at the start
   of today.
5. The result takes the form's place. **Open Bills tab** shows the new bills,
   with `Updated` dates inside the days chosen, in state `pending`, each
   made out to a customer's name.
6. Shop hours: Settings → Admin → **Shop hours for a batch**, then **Save
   hours**; the dialog's right side shows them the next time it opens.
