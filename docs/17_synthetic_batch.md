# Synthetic Batch Data Scheduler

Ported from V1 (`Tally_Wrapper/services/synthetic_bill_generator.py` +
`Tally_Wrapper/ui/synthetic_bill_scheduler_dialog.py`). Generates a cluster of
realistic `pending` bills with backfilled audit timestamps, for QA / volume
padding / business-day replay. Available in production, admin-gated.

## Surface

- **API:** `POST /api/bills/synthetic-batch` — `[Authorize(Policy = "Admin")]`, takes
  [`SyntheticBatchRequest`](../src/ShowroomBilling.Contracts/Bills/SyntheticBatchContracts.cs)
  and returns `SyntheticBatchResponse`.
- **Desktop:** Settings → Advanced → **Open Batch Data Scheduler…** opens the
  [`SyntheticBatchDialog`](../src/ShowroomBilling.Desktop/Views/SyntheticBatch/SyntheticBatchDialog.xaml).
  Admin unlock is prompted automatically if the session is locked.

## Input model (V1-parity)

| Field | Units | Rule |
|---|---|---|
| `TotalAmount` | ₹ | partitioned into bills |
| `MaxBillAmount` | ₹ | ≤ `₹1,99,000` (compliance cap; cannot be relaxed) |
| `Rate24Kt` | ₹/g | > 0 |
| `PaymentMode` | string | `Cash` → party "Cash"; else → "Credit and debit" |
| `MinItemsPerBill` / `MaxItemsPerBill` | int | `1..25`, min ≤ max |
| `StartAtUtc` / `EndAtUtc` | UTC | start < end; window must have ≥ bill-count minute slots |
| `SelectedKaratLabels` | list | must be non-empty subset of Tally-mapped karats |

## Algorithm

1. `PartitionBillTotals` — partitions `TotalAmount` into random per-bill totals
   drawn uniformly from `[₹25,000, MaxBillAmount]`. Any remainder under
   `₹25,000` is dropped (matches V1).
2. `BuildRandomSchedule` — samples `N` distinct minute-slots from the interval
   `[ceil_minute(Start), floor_minute(End)]` without replacement, sorted
   ascending. If a `floorUtc` (latest existing `Bill.CreatedAtUtc`) is supplied,
   no slot lands at or before it — preserves causal ordering across runs.
3. `BuildLineTargets` — splits each per-bill total into 1..`MaxItemsPerBill`
   slices via 8-attempt rejection sampling (each slice ≥ `₹500` when feasible).
   Falls back to an even split if rejection fails.
4. `BuildLineForTarget` — for each slice, picks a random
   `ItemMasterEntry` × (Tally-mapped) `KaratMasterEntry`, computes
   `effective_rate` via [`BillCalculator`](../src/ShowroomBilling.Application/Bills/BillCalculator.cs),
   derives `qty` from `target/effRate × uniform(0.35, 0.9)`. Probes the computed
   inclusive total; if it overshoots the target, halves the qty, then clamps to
   `MinQty = 0.001 g`.
5. `SyntheticBatchExecutor` — loops planned bills, calls
   [`IBillService.CreateBackdatedDraftAsync`](../src/ShowroomBilling.Application/Bills/IBillService.cs)
   with the planned `ScheduledAtUtc`. Each bill reserves the next invoice number
   via the existing `NumberingService` (real-now — numbering is monotonic and
   independent of business date), then persists bill + revision + the
   `bill.pending.created` audit event, all stamped with the backfilled time.

## Invariants

- **Bills land in `pending`.** Identical to a user-saved draft; see
  [`docs/03_bill_state_machine.md`](03_bill_state_machine.md).
- **No Tally traffic.** The executor never calls `ITallyPoster`; the only
  write path is `CreateBackdatedDraftAsync`.
- **Per-bill grand total ≤ ~₹1,99,000 × 1.03** (₹1,99,000 is the partition cap
  on the exclusive-of-GST target; the inclusive-of-GST grand total can be up
  to ~3% higher before round-off).
- **No two bills share a minute-slot.** Enforced by
  `SampleWithoutReplacement` in `BuildRandomSchedule` and verified by the
  `BuildRandomSchedule_ProducesSortedDistinctMinuteSlots` test.
- **Audit events carry the backfilled timestamp.** Verified by
  `CreateBackdatedDraftAsync_UsesOverrideForBillAndAudit`.

## Constants (do not relax without product sign-off)

| Symbol | Value | V1 source |
|---|---|---|
| `SoftMinBillTotal` | `₹25,000` | `synthetic_bill_generator.py:15` |
| `HardMaxBillAmount` | `₹1,99,000` | `synthetic_bill_generator.py:143` |
| `MinItemTarget` | `₹500` | `synthetic_bill_generator.py:16` |
| `MinQty` | `0.001 g` | `synthetic_bill_generator.py:17` |
| `QtyFraction` | `uniform(0.35, 0.9)` | `synthetic_bill_generator.py:391` |
| `LineCountRetryAttempts` | `8` | `synthetic_bill_generator.py:310` |
| `MaxItemsPerBillCap` | `25` | `synthetic_bill_generator.py:115` (UI spin max) |

## Running it

### API smoke

```powershell
# 1. Start the API
dotnet run --project src/ShowroomBilling.Api

# 2. Unlock admin (swagger or curl)
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

1. Launch the Desktop (`dotnet run --project src/ShowroomBilling.Desktop`).
2. Press `~` to unlock admin.
3. Navigate **Settings → Advanced → Open Batch Data Scheduler…**
4. Fill the form and click **Start Schedule**.
5. The **Bills** tab refreshes automatically on completion; new bills appear
   with `Updated` timestamps inside the selected window, in state `pending`.
