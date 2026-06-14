# Tally Integration Contract

This document defines the Tally Wrapper contract between the desktop client, the cloud API, and TallyPrime.

**Architectural note:** Tally Wrapper no longer ships a separate `TallyBridge` process or a posting queue. The API talks to Tally XML directly, in-process, only when an operator clicks Push (or Refresh for masters). Every Tally interaction is synchronous and initiated by a human click.

Direction already chosen:

- Windows-first WPF desktop
- ASP.NET Core modular monolith backend (runs on the same machine as TallyPrime)
- backend can run either as the desktop-owned embedded API or as a Windows Service on the Tally server for LAN workstations
- PostgreSQL system of record
- no separate bridge process, no job queue, no polling
- no local durable business storage on the desktop

---

## 1. Responsibility split

### 1.1 Desktop client

The desktop does:

- create and edit pending bills
- manually push bills to the cloud API for Tally posting (one click = one synchronous API call that posts to Tally and returns the result)
- show bill history and state
- request retry/repost/void/revise/edit/mark-posted/mark-pending/delete actions
- preview and print invoices
- show settings, admin, and recovery UI

The desktop does **not**:

- talk directly to TallyPrime
- own Tally posting truth
- own any background notification or retry mechanism

In `Server` connection mode, the desktop calls `http://<tally-server>:5107` and does not start a local API child process. Switching back to `LocalEmbedded` is a per-workstation fallback and requires that workstation to have its own DB override and Tally host settings that point at the Tally server over the LAN.

### 1.2 Cloud API

The API does everything else:

- own bill truth
- validate and finalize manually pushed bills
- allocate invoice numbers (reserved at save, not at push)
- build canonical Tally sales-voucher XML via `TallyXmlVoucherBuilder`
- post XML to TallyPrime on the LAN via `ITallyPoster` → `ITallyXmlClient`
- classify posting results inline in the same HTTP request
- store posting outcomes as audit events on the bill
- fetch Tally masters on operator demand via `ITallyMasterRefresher`
- expose masters to desktop from server-owned snapshots
- recover stuck `posting` bills on API boot (`StuckPostingRecoveryHostedService`)

### 1.3 TallyPrime

TallyPrime remains:

- the LAN-local accounting endpoint
- the source for company, ledger, stock item, and voucher type lists
- the consumer of canonical sales voucher XML

The server tray is a maintenance/status companion for the API Windows Service. It may show API/DB/client status and control the Windows Service, but it must not poll Tally. Tally checks still happen only through operator-triggered Push, Refresh, and System Health flows.

---

## 2. What data is fetched from Tally

The API, on operator demand only, fetches and stores normalized snapshots for:

- company list
- ledger list
- stock item list
- voucher type list

**Cadence:** zero. There is no timer. The only trigger is a click on "Refresh from Tally" in Settings or the System Health dialog, which issues `POST /api/masters/refresh`. Each call blocks until Tally answers and writes the fresh snapshot.

Desktop reads these through the API, never from Tally directly.

The canonical XML request/response shapes, `<FETCH>` field lists per master type, response quirks (C0 control characters, sentinel values like `\x04 Not Found` for unset HSN, mandatory `<SVCURRENTCOMPANY>` scoping for every master except the company list), and live counts observed against real Tally are documented in [`06_tally_xml_golden_path.md`](06_tally_xml_golden_path.md) section 7 (Masters read path).

---

## 3. What is posted to Tally

Tally Wrapper initial posting scope:

- sales vouchers only

Posting payload source:

- immutable bill revision snapshot from PostgreSQL

Posting format:

- canonical sales-voucher XML golden path from [`06_tally_xml_golden_path.md`](06_tally_xml_golden_path.md)

Posting trigger:

- operator click on Push (or Retry, Repost, Push Selected, Push All Pending). No other trigger exists.

### 3.1 Party-ledger derivation (payment mode → Tally ledger)

The `<PARTYLEDGERNAME>` Tally sees is **never** the operator's free-text customer label. It is resolved deterministically from the bill's payment mode through the operator-configured ledger map. V1 parity (`tally/xml_builder.py`).

1. **Normalize the payment mode.** `PaymentMode.Normalize(bill.Payment)` collapses every input into one of two canonical buckets:
   - `"Cash"` — case-insensitive match on the literal string `Cash`.
   - `"Credit and debit"` — every other non-empty value (`Card`, `UPI`, `Bank Transfer`, `Cheque`, `DD`, `Net Banking`, …) and every blank/null value.
2. **Resolve to a Tally ledger** via `LedgerMappingsDto`:
   - `Cash` → `Settings.Ledgers.CashLedger`
   - `Credit and debit` → `Settings.Ledgers.CreditDebitLedger`
3. **Validate.** A blank resolved ledger fails the build with `CONFIG_MISSING_CASH_LEDGER` or `CONFIG_MISSING_CREDIT_DEBIT_LEDGER` (terminal). A blank `bill.Payment` fails with `MISSING_PAYMENT_MODE` (terminal). The bill lands in `failed`; the operator fixes Settings and clicks Retry.
4. **Use the resolved ledger** for both `<PARTYLEDGERNAME>` and the offsetting Dr leg under `<ALLLEDGERENTRIES.LIST>`.

**The free-text `bill.PartyName`** (e.g. `"Mr. Sharma — walk-in"`) is operator/print-only. It is joined with `bill.Notes` by `" | "` and emitted as `<NARRATION>` so the customer label is preserved on the Tally voucher for human reference, but it never reaches `<PARTYLEDGERNAME>` and is never used to look up a Tally ledger.

This split exists because the customer label is unbounded free text and would never match a ledger; the payment-mode → ledger map is the only deterministic mapping the operator controls. If the showroom moves all card/UPI receipts to a single bank account ledger today and a different one tomorrow, only `Settings.Ledgers.CreditDebitLedger` changes — bills, payloads, and validation rules stay untouched.

The single shared helper is [`PaymentMode`](../src/ShowroomBilling.Contracts/Bills/PaymentMode.cs) in `ShowroomBilling.Contracts.Bills`. Use it from any code that reads or normalizes the payment field — voucher builder, synthetic batch planner, and the Invoice payment dropdown all share it.

### 3.2 GST state/country fields

The voucher builder emits the party and location fields Tally uses for GST return readiness:

- resolved party ledger → `<PARTYLEDGERNAME>`, `<PARTYNAME>`, and the debit ledger entry's `<ISPARTYLEDGER>Yes</ISPARTYLEDGER>`
- all sales posted by this app → `<GSTREGISTRATIONTYPE>Unregistered/Consumer</GSTREGISTRATIONTYPE>`
- `Print.CompanyState` → `<STATENAME>`, `<PLACEOFSUPPLY>`, `<CONSIGNEESTATENAME>`
- `Print.CompanyCountry` → `<COUNTRYNAME>`, `<COUNTRYOFRESIDENCE>`, `<CONSIGNEECOUNTRYNAME>`

`COUNTRYNAME` is enough for the sales voucher screen to show a country, but GSTR-1 uses the GST transaction storages (`COUNTRYOFRESIDENCE` / `CONSIGNEECOUNTRYNAME`). Tally Wrapper bills do not collect a separate buyer country for counter sales, so the configured invoice country is the domestic default for those voucher fields.

Do not emit a free-text customer label as `<PARTYNAME>`: Tally expects the party ledger there. Do not emit `<PARTYGSTIN>` or `<CONSIGNEEGSTIN>` from this billing app; this workflow is only for unregistered/consumer sales. The operator-entered customer label remains narration/printing context.

---

## 4. Posting model

**Synchronous, in-process, manual only.**

When the operator clicks Push on the Bills tab, the desktop calls `POST /api/bills/{billId}/push`. Inside that HTTP request, `BillService.PushInternalAsync`:

1. Checks Tally company health via `ITallyCompanyHealthService`. Tally must be reachable and the configured active company must be open. If not, the API returns `503 Tally unavailable` and leaves the bill in its original state.
2. Atomically transitions the bill to `posting` via a conditional `UPDATE bills SET state='posting' WHERE id=@id AND state IN ('pending','draft','failed')`. If 0 rows are affected — another concurrent push already won the flip, or the state drifted — the request short-circuits and returns the current bill without a second Tally call. Closes a double-click / duplicate-request race that would otherwise produce two vouchers.
3. Chooses Tally operation: normal pushes/retries/reposts use create XML; bills reopened after a prior successful push (`EditedAfterPush=true`) use alter XML against the old Tally voucher `MASTER ID`.
4. Calls `ITallyPoster.PostAsync` — this builds voucher XML, sends it to Tally via HTTP, parses the response, and returns an outcome.
5. Writes `tally.posted` or `tally.failed` audit and transitions the bill to `posted` or `failed`.
6. Returns the resulting `BillResponse` to the desktop.

If an edited-after-push bill cannot resolve the prior Tally `MASTER ID` from its pre-edit `tally.posted` audit, the API settles the attempt as `failed` with `TALLY_ALTER_TARGET_MISSING` and does not call Tally. It never falls back to creating a second voucher for that edited bill.

The conditional flip and the Tally call are deliberately **not** wrapped in a single transaction: `posting` must be durably visible in the DB before the Tally round-trip, so `StuckPostingRecoveryHostedService` can heal the row if the API crashes mid-call.

The desktop's button stays "busy" for the duration of the call (typically 1–10 seconds against Tally). The click returns only when Tally has answered or the call has failed.

### Why this model

- Simplest possible topology: a click is a single HTTP request end-to-end.
- No queue to drain, no poll interval to tune, no bridge to monitor.
- If Tally is down, the click fails loudly; the operator sees the error immediately and tries again later. No silent queueing.
- If the API is down, the desktop surfaces "Cloud unavailable"; no work is lost — bills stay in `pending`.

### Crash recovery

If the API process dies mid-call, any bill stuck in `posting` is flipped back to `pending` by `StuckPostingRecoveryHostedService` on the next API boot, with a `bill.posting.recovered` audit event. The operator retries.

---

## 5. Failure classification

### 5.1 Retryable failures (bill lands in `failed`, operator clicks Retry)

- Tally transport failure after a successful preflight
- DNS/LAN connectivity issue after a successful preflight
- timeout after a successful preflight
- connection refused after a successful preflight
- transient HTTP transport failure

### 5.2 Non-retryable failures (bill lands in `failed`, operator fixes config/data then Retry or Revise)

- missing ledger, stock item, voucher type
- company mismatch
- period lock
- malformed business payload
- Tally business rejection

Because there is no automatic retry, the desktop's classification is informational only — the operator decides what to do next. `BillPostingStatusResponse.LastErrorCode` / `LastErrorMessage` carry enough detail to tell retryable and non-retryable apart.

### 5.3 Ambiguous outcome

If Tally may have accepted the voucher but the HTTP response is unreadable, the bill lands in `failed`. The operator verifies in Tally itself, then either:

- clicks **Mark as Pushed** (with a reason ≥4 chars) — transitions the bill to `posted` without re-calling Tally, or
- clicks **Retry** — re-posts, which may succeed (Tally deduplicates via `REMOTEID`) or surface a duplicate error.

There is no automatic reconciliation path; the operator is the reconciler.

---

## 6. Operational failure scenarios

| Scenario | Expected behavior |
|---|---|
| Tally down or wrong active company, API up | Push is blocked by preflight with `503 Tally unavailable`; the bill remains `pending`/`failed`/`posted` as it was. Operator opens Tally or the correct company, then retries. |
| API down | Desktop shows "Cloud unavailable"; bills stay in `pending` on whatever was last persisted; clicking Push is disabled. |
| API crashes mid-post | Bill is stuck in `posting` until API restart; `StuckPostingRecoveryHostedService` flips it back to `pending` on boot with an audit breadcrumb. Operator retries. |
| Desktop down during a push | That push fails; no correctness lost (the API either completed the post or crashed; either way recovery is deterministic). |
| Network partitions after preflight / mid-post | Timeout → bill in `failed` → operator verifies in Tally → Mark as Pushed or Retry. |

---

## 7. Ownership summary

| Concern | Desktop | API |
|---|---:|---:|
| Pending-bill editing | Yes | Yes |
| Final validation | No | Yes |
| Numbering | No | Yes (reserved on save) |
| Voucher XML building | No | Yes |
| Tally posting | No | Yes (synchronous, inline) |
| Master refresh | No (just click) | Yes (synchronous on request) |
| Final status truth | No | Yes |
| Master snapshot truth | No | Yes |
| Local durable business state | No | Yes (PostgreSQL) |

---

## 8. No local durable business storage rule

### Allowed on desktop

- bootstrap config (API URL, counter name)
- print preferences (last-used printer, last PDF directory)
- admin unlock token cache (TTL-bounded)
- logs

### Not allowed on desktop

- bills
- posting queue (no queue exists on either side — both sides are stateless-posting)
- master-data snapshots as authoritative cache (desktop reads them through the API each time)
- numbering state

### Allowed on API (the single source of truth)

- every business table in PostgreSQL
- Tally connection config in cloud settings
- operator-fetched master snapshots (updated only on click)

---

## 9. Implementation guidance

- `ITallyPoster` + `TallyXmlClient` live in `ShowroomBilling.Infrastructure.Tally`. They're in-process services, not hosted workers.
- `ITallyMasterRefresher` follows the same pattern for master data.
- Edited-after-push bills alter the existing Tally voucher by `MASTER ID`; plain first push, retry, and unedited repost keep create/import semantics.
- Push, retry, repost, selected push, and push-all-pending must run a Tally company preflight before any bill is moved to `posting`. Batch push probes once before iterating.
- **No business-level retry loop.** Every bill-level retry is an explicit operator click — `/retry`, `/repost`, re-pushing after fixing config. The state machine is not "eventually consistent".
- The HTTP client under `ITallyXmlClient` has a **transport-layer** Polly pipeline (2 retries, ~200 ms jittered backoff, triggered on `HttpRequestException` + 5xx) to absorb single transient blips. This does not re-run Tally body errors (`LINEERROR`, `EXCEPTIONS > 0`) — those flow straight to `failed` and wait for the operator. Treat this as a connect-fail smoother, not a retry strategy.
- Do not re-introduce a queue, polling loop, or second process as an "optimization." The architecture was explicitly collapsed from two-process to single-process; reversing it would re-open the whole class of bugs we removed.
