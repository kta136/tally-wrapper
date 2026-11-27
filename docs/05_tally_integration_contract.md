# Tally Integration Contract

This document defines the Tally Wrapper contract between the desktop client, the cloud API, and TallyPrime.

**Architectural note:** There is no separate bridge process and no posting queue. The API talks to Tally XML directly, in-process, only when an operator clicks Push (or Refresh for masters). Every Tally interaction is synchronous and initiated by a human click.

Direction already chosen:

- Windows-first WPF desktop
- ASP.NET Core modular monolith backend (runs on the same machine as TallyPrime)
- the backend runs as a Windows Service on the Tally server; every counter's desktop reaches it over the LAN
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
- start or host an API

The desktop calls the server API at the address saved on the workstation in Settings → Server (`http://<tally-server>:5107`). Without a reachable server it shows the limited-mode screen and cannot save bills.

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
- recover stuck `posting` bills at API start and every 5 minutes after (`StuckPostingRecoveryHostedService`; database only, it never contacts Tally)

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

**Cadence:** zero. There is no timer. The only trigger is a click on "Refresh from Tally" in Settings or the System Health dialog, which issues `POST /api/masters/refresh`. Each call blocks until Tally answers and writes the fresh snapshot, which replaces the previous one of that master type.

Desktop reads these through the API, never from Tally directly.

The canonical XML request/response shapes, `<FETCH>` field lists per master type, response quirks (C0 control characters, sentinel values like `\x04 Not Found` for unset HSN, mandatory `<SVCURRENTCOMPANY>` scoping for every master except the company list), and live counts observed against real Tally are documented in [`06_tally_xml_golden_path.md`](06_tally_xml_golden_path.md) section 3 (Masters read path).

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

**The free-text `bill.PartyName`** (e.g. `"Mr. Sharma — walk-in"`) never reaches `<PARTYLEDGERNAME>` and is never used to look up a Tally ledger. It goes onto the voucher in two places, both free text:

- `<PARTYMAILINGNAME>`, the buyer's mailing name in the voucher's party details, which takes free text. A bill with no customer name sends the resolved party ledger there instead, so an alter of a bill whose name was removed also replaces the old name. Not yet pushed to a live company ([docs/06 §4.2](06_tally_xml_golden_path.md)).
- `<NARRATION>`, joined with `bill.Notes` by `" | "`, left out when both are blank.

This split exists because the customer label is unbounded free text and would never match a ledger; the payment-mode → ledger map is the only deterministic mapping the operator controls. If the showroom moves all card/UPI receipts to a single bank account ledger today and a different one tomorrow, only `Settings.Ledgers.CreditDebitLedger` changes — bills, payloads, and validation rules stay untouched.

The single shared helper is [`PaymentMode`](../src/ShowroomBilling.Contracts/Bills/PaymentMode.cs) in `ShowroomBilling.Contracts.Bills`. Use it from any code that reads or normalizes the payment field — voucher builder, synthetic batch planner, and the Invoice payment dropdown all share it.

### 3.2 GST state/country fields

The voucher builder emits the party and location fields Tally uses for GST return readiness:

- resolved party ledger → `<PARTYLEDGERNAME>`, `<PARTYNAME>`, and the debit ledger entry's `<ISPARTYLEDGER>Yes</ISPARTYLEDGER>`
- `bill.PartyName` (or the party ledger, when the bill names no customer) → `<PARTYMAILINGNAME>`
- all sales posted by this app → `<GSTREGISTRATIONTYPE>Unregistered/Consumer</GSTREGISTRATIONTYPE>`
- `Print.CompanyState` → `<STATENAME>`, `<PLACEOFSUPPLY>`, `<CONSIGNEESTATENAME>`
- `Print.CompanyCountry` → `<COUNTRYNAME>`, `<COUNTRYOFRESIDENCE>`, `<CONSIGNEECOUNTRYNAME>`

`COUNTRYNAME` is enough for the sales voucher screen to show a country, but GSTR-1 uses the GST transaction storages (`COUNTRYOFRESIDENCE` / `CONSIGNEECOUNTRYNAME`). Tally Wrapper bills do not collect a separate buyer country for counter sales, so the configured invoice country is the domestic default for those voucher fields.

Do not emit a free-text customer label as `<PARTYNAME>`: Tally expects the party ledger there. The customer's name goes in `<PARTYMAILINGNAME>` (§3.1). Do not emit `<PARTYGSTIN>` or `<CONSIGNEEGSTIN>` from this billing app; this workflow is only for unregistered/consumer sales.

---

## 4. Posting model

**Synchronous, in-process, manual only.**

When the operator clicks Push on the Bills tab, the desktop calls `POST /api/bills/{billId}/push`. Inside that HTTP request, `BillPostingWorkflow.PushInternalAsync`:

1. Checks Tally company health via `ITallyCompanyHealthService`. Tally must be reachable and the configured active company must be open. If not, the API returns `503 Tally unavailable` and leaves the bill in its original state.
2. Atomically transitions the bill to `posting` via a conditional `UPDATE bills SET state='posting' WHERE id=@id AND state IN ('pending','draft','failed')`. If 0 rows are affected — another concurrent push already won the flip, or the state drifted — the request short-circuits and returns the current bill without a second Tally call. Closes a double-click / duplicate-request race that would otherwise produce two vouchers.
3. Chooses Tally operation: normal pushes/retries/reposts use create XML; bills reopened after a prior successful push (`EditedAfterPush=true`) use alter XML against the old Tally voucher, named by its `MASTER ID` and the date it has in Tally (read from Tally first, below).
4. Calls `ITallyPoster.PostAsync` — this builds voucher XML, sends it to Tally via HTTP, parses the response, and returns an outcome.
5. Writes `tally.posted`, `tally.failed`, or `tally.outcome.unknown` and transitions the bill to `posted`, `failed`, or `reconciliation_required`.
6. When Tally accepted the voucher, reads it back by its master id and records the number, date and total Tally filed it under (`tally.voucher.filed`). With Automatic numbering Tally ignores the number sent and gives its own ([06 §4.4.1](06_tally_xml_golden_path.md)). This is a read made after the outcome is saved; it never changes the push, and one that fails is only logged.
7. Returns the resulting `BillResponse` to the desktop, with the number Tally filed the voucher under as `tallyVoucherNumber`.

An alter names its voucher by `MASTER ID` **and by the date the voucher has in Tally now**. Tally matches on both, and an alter it cannot match it imports as a new voucher, with no error: that is how an edited bill's push made a second voucher in a live company on 25 November 2026 ([06 §4.3](06_tally_xml_golden_path.md)). Only Tally can say the date (the bill's own date may have been edited, or the voucher's changed in Tally by hand), so before it claims the bill the API always reads the voucher from Tally (`ITallyVoucherLookup`, a read; the requests are in [06 §4.3](06_tally_xml_golden_path.md)):

- **The company comes first.** A master id names a voucher only within its company, so `tally.posted` records the company each accepted voucher went to (since 25 November 2026). When the last pre-edit push names another company than Settings do now, the push fails with `TALLY_COMPANY_CHANGED` before either read, and nothing is sent. The company and voucher type the read was made in travel with the alter, and the poster refuses to send it (`TALLY_COMPANY_CHANGED`) if Settings changed in between.
- **A bill a push from here succeeded for:** its pre-edit `tally.posted` audit records the id, and Tally is asked for the sales voucher with that id in the bill's fiscal year. Found: it is altered, named by that id and that date. Not found (deleted in Tally, or dated outside the fiscal year): `failed` with `TALLY_VOUCHER_NOT_FOUND`, nothing sent; the admin checks Tally, and a voucher that is not there at all is answered with Mark as Pending, so the next push creates it. A push recorded before pushes named their company is held to the bill as well: the voucher must show the date and grand total of the revision Tally was last said to hold, or the push fails with `TALLY_VOUCHER_MISMATCH`. Only `tallyMasterId` names the voucher; `remoteId` stands in for it only on an audit from before `tallyMasterId` was recorded, because since then it can be a repost's own key. A Mark as Pending ends what an earlier push says: after it, only a later push or Mark as Pushed says what Tally holds.
- **A bill an admin marked as pushed** has no such record: its voucher was entered in Tally by hand, or came from a push whose answer was lost. Tally is asked which sales vouchers carry the bill's number in its fiscal year. The number asked for is the one the bill had when Tally last held it, so a rename made since the mark looks for the old number.
  - **Exactly one voucher, showing the bill as it was marked:** its date and total (the party amount) must equal the date and grand total of the revision that was current when the bill was marked as pushed. A number is not a voucher's identity: when the voucher type numbers vouchers itself (Automatic, Auto Renumber), an insertion moves every later number, and the one voucher now carrying the bill's number can be another sale. It is altered, and the `tally.posted` audit records its id, so later edits ask Tally for that id instead of the number.
  - **Exactly one voucher, showing something else** (or no total): `failed` with `TALLY_VOUCHER_MISMATCH`, nothing sent. The admin changes the voucher in Tally by hand and uses Mark as Pushed, or, when the bill's voucher is not there at all, Mark as Pending.
  - **Known limit:** two sales of the same day with the same total cannot be told apart this way. If Auto Renumber moves the other one onto the bill's old number, it passes the check. Closing that needs the voucher's id recorded when the bill is marked as pushed, which Mark as Pushed does not ask Tally for today.
  - **None:** the attempt is settled as `failed` with `TALLY_VOUCHER_NOT_FOUND`. Nothing is sent. The admin checks Tally: a voucher that is there under another number is renumbered in Tally and the push retried; one that is not there at all is answered with Mark as Pending, and the next push creates it.
  - **More than one:** `failed` with `TALLY_VOUCHER_AMBIGUOUS`. Nothing is sent until the extra voucher is removed or renumbered in Tally.
- **Tally cannot be asked** (unreachable, timeout, unreadable answer, an answer that is not a list of vouchers, one id listed with two dates; either read): the push is refused like a failed preflight (503) and the bill stays as it was.

The answer is checked again on this side: a voucher counts only when its own id or number is the one asked for, its type is the sales voucher type, its date is inside the fiscal year asked for and it is not kept only as deleted, so a filter or period Tally ignored finds nothing rather than a stranger's voucher. The API never falls back to creating a second voucher for an edited bill. `TALLY_ALTER_TARGET_MISSING` remains for the case where no lookup is available. The `bill.push.requested` audit of an alter records the date it named as `voucherDateInTally`.

The conditional flip and the Tally call are deliberately **not** wrapped in a single transaction: `posting` must be durably visible in the DB before the Tally round-trip, so `StuckPostingRecoveryHostedService` can heal the row if the API crashes mid-call.

The desktop's button stays "busy" for the duration of the call (typically 1–10 seconds against Tally). The click returns only when Tally has answered or the call has failed. When Tally filed the voucher under another number than the bill's, the Bills tab's status line says so ("Pushed SR/1. Tally filed it as SR/2: see Settings → Tally check.") and the bill's row note reads "In Tally as …".

### Settings → Tally check

A read, run only when an operator presses **Compare with Tally** (`GET /api/tally-check/compare`, [09 §2.8](09_api_spec.md)). The server asks Tally for every sales voucher of the fiscal year in one request ([06 §4.4.2](06_tally_xml_golden_path.md)) and sets them against the bills Tally should hold: posted, edited after a push, or last said to be in Tally by a push or Mark as Pushed, and the bills waiting for reconciliation, which this check helps settle. Pending, draft, voided and failed bills are not compared.

- A bill is set against the voucher its last push recorded, by master id. A master id names a voucher only within its company, so a bill whose last push went to another company than Settings name now is not compared; the answer counts such bills. A bill no push recorded an id for (marked as pushed by hand, or pushed before ids were recorded) is matched by the number Tally was last said to hold it under, allowing zeros in front of any run of digits ("…/051" for "…/51", "SR/051/26-27" for "SR/51/26-27"), and a voucher with the bill's date and total is preferred among several.
- The bill's side is the bill as Tally holds it: as it is now, or, for a bill edited after Tally took it, the revision current when Tally was last said to hold it (the bill's first revision when none is that old, as with bills brought over from V1, whose revisions can be dated after the push). So an edit not yet pushed is not a difference. For a bill waiting for reconciliation it is the revision and number its unsettled push sent, since whether that push landed is the question; it is looked for by the voucher an unsettled Alter named, and by its number after an unsettled Create (an earlier push's voucher says nothing about a fresh create, such as a repost after that voucher was deleted).
- A number Tally writes with zeros in front of its digits (its voucher type fills numbers to a width: "…/051" for "…/51") or with its letters in another case is the same number. Such a bill counts as matching, and the answer says how many there are (one line on the screen, not a row each).
- It lists what differs: the number, the date or total (a total Tally did not give counts as differing, since it could not be checked), a bill whose voucher Tally does not have (**missing from Tally**), a voucher no bill accounts for (**extra in Tally**), and two bills whose pushes recorded the same voucher. A bill not found is not proof that its voucher is gone: only the sales voucher type Settings name, dated in the year, is read, and a bill looked for by number may be filed under another, so the operator is told to find it in Tally first. Among several vouchers with the bill's number, one with its date and total is taken, then one written exactly as the bill's number, so the result does not depend on the order Tally lists them in.
- Tally is read before the bills, so a push that settles in between (even with an outcome nobody knows) can show as a difference that is not one. The answer says when a push settled, or was running, during the comparison, and the operator compares again. The company the vouchers were read in comes from the same read of Settings as the list.

Nothing is changed on either side. **Open bill** shows the bill in Bill details with its history; the operator fixes it from its row in the Bills tab (edit and push, or Mark as Pushed / Mark as Pending after checking Tally). A voucher with no bill is looked at in Tally. The table shows 100 rows at a time. The result is kept by the Desktop only while it is open.

#### What each kind of row usually is, and what to do

From the first run against a live company (26 November 2026):

- **Extra zeros** ("…/051" for "…/51"): the voucher type fills Tally's own numbers to a width. Counted as matching; nothing to do.
- **Same-day swaps** (two or more bills of one day, each filed under another's number, same dates and totals): Auto Renumber numbers a day's vouchers in the order they were created, and the bills were pushed out of number order ([06 §4.4.1](06_tally_xml_golden_path.md)). Since 26 November 2026 the app asks before a push or retry that would do this, and pushes ticked bills in date and number order. Nothing in the app changes them, and Tally does not let a number be typed under Auto Renumber; leave them, or let whoever keeps the books decide. Do not change the voucher type's numbering method to fix them.
- **A number and total difference beside a missing bill and an extra voucher, all on one day**: one record problem, not three. A push recorded the id of the next bill's voucher (an old push whose `remoteId` stands in for the id), so the bill is set against the wrong voucher, and the next bill, marked as pushed by hand, finds its own voucher already taken. Check in Tally that each bill's voucher is there under its number; then an admin uses **Mark as Pending** and **Mark as Pushed** on the first bill. Mark as Pending ends what the old push recorded, so the bill is matched by its number from then on. Neither touches Tally.
- **A real difference in date, total or lines**: decide which side is right.
  - *Tally is right* (the voucher was changed there by hand): edit the bill in the app to the voucher's figures (date, weights, rates, discount, bank charges, payment mode), save, and have an admin use **Mark as Pushed**. Do not push it: a push sends the bill to Tally again, and for a bill pushed before 25 November 2026 it is refused anyway, because the voucher no longer shows the bill as it was pushed (`TALLY_VOUCHER_MISMATCH`), and the bill is left `failed`. Compare again afterwards: the row goes.
  - *The bill is right*: correct the voucher in Tally by hand, for the same reason.
- **Bills brought over from V1** carry no karat on their gold lines. Opened for editing, such a line is priced at 0% purity ("Pick a karat to price this line") and the bill shows a fraction of its total; pick the karat first and the total comes back. Do not save one without it.
- The **Final amount** box only brings a bill down to a figure (it sets a discount). To raise a bill to a voucher's total, change the weight, wastage or extra first, then set the final amount to settle the last rupee.

### Why this model

- Simplest possible topology: a click is a single HTTP request end-to-end.
- No queue to drain, no poll interval to tune, no bridge to monitor.
- If Tally is down, the click fails loudly; the operator sees the error immediately and tries again later. No silent queueing.
- If the API is down, the desktop surfaces "Cloud unavailable"; no work is lost — bills stay in `pending`.

### Crash recovery and uncertain writes

If the API process dies mid-call, any bill stuck in `posting` moves to `reconciliation_required` on the next API boot, with a `bill.posting.recovered` audit event. Because Tally may already have accepted the voucher, the operator must verify Tally and then use admin **Mark as Pushed** or **Mark as Pending**. Recovery never automatically requeues an uncertain write.

---

## 5. Failure classification

### 5.1 Ambiguous transport outcomes (bill lands in `reconciliation_required`)

- Tally transport failure once the connection to Tally was open
- timeout after a successful preflight
- HTTP error after the voucher request was sent
- empty, malformed, or otherwise unreadable Tally response

These outcomes are not auto-retried: a retry could create a duplicate voucher. The operator verifies Tally first, then resolves the state explicitly.

### 5.2 Definite failures (bill lands in `failed`, operator fixes config/data then Retry or Edit)

- missing ledger, stock item, voucher type
- company mismatch
- period lock
- malformed business payload
- Tally business rejection
- the connection to Tally was refused, or its host name did not resolve, after a successful preflight (`TALLY_UNREACHABLE`: Tally was closed in between). The connection was never made, so no byte of the voucher was sent and a plain Retry is safe

Explicit Tally business rejections (`LINEERROR`, import errors, and classified configuration/data failures) are definite failures, unless the same response also reports `CREATED`/`ALTERED > 0`; that mixed response moves the bill to `reconciliation_required`. `BillPostingStatusResponse.LastErrorCode` / `LastErrorMessage` surface the latest definite or ambiguous problem.

### 5.3 Ambiguous outcome

If Tally may have accepted the voucher but the HTTP response is unreadable, the bill lands in `reconciliation_required`. Normal edit, retry, repost, renumber, void, and delete actions are blocked. The operator verifies in Tally itself, then either:

- clicks **Mark as Pushed** (with a reason ≥4 chars) — transitions the bill to `posted` without re-calling Tally, or
- clicks **Mark as Pending** (with a reason ≥4 chars) after confirming no voucher exists, then pushes again.

There is no automatic reconciliation path; the operator is the reconciler.

---

## 6. Operational failure scenarios

| Scenario | Expected behavior |
|---|---|
| Tally down or wrong active company, API up | Push is blocked by preflight with `503 Tally unavailable`; the bill remains `pending`/`failed`/`posted` as it was. Operator opens Tally or the correct company, then retries. |
| API down | Desktop shows "Cloud unavailable"; bills stay in `pending` on whatever was last persisted; clicking Push is disabled. |
| API crashes mid-post, or loses the database before it can record the outcome | Bill is left in `posting`. `StuckPostingRecoveryHostedService`, which sweeps at API start and every 5 minutes after, moves it to `reconciliation_required` with an audit breadcrumb once the claim is older than 10 minutes (longer than any push can run, so a push still running in another API process on the same database is never touched). No restart is needed. Operator verifies Tally before resolving it. |
| Caller disconnects mid-post | The push keeps its own 30 s settle budget and records the known or unknown outcome; it is never left in `posting`. A cancellation noticed before anything is sent restores the prior state. |
| Desktop down during a push | That push fails; no correctness lost (the API either completed the post or crashed; either way recovery is deterministic). |
| Network partitions after preflight / mid-post | Timeout → `reconciliation_required` → operator verifies in Tally → Mark as Pushed or Mark as Pending. |

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

- the server address
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
- Edited-after-push bills alter the existing Tally voucher by `MASTER ID` and the date Tally holds for it, both read from Tally before the claim; plain first push, retry, and unedited repost keep create/import semantics.
- Push, retry, repost, selected push, and push-all-pending must run a Tally company preflight before any bill is moved to `posting`. Batch push probes once before iterating.
- **No business-level retry loop.** Every bill-level retry is an explicit operator click — `/retry`, `/repost`, re-pushing after fixing config. The state machine is not "eventually consistent".
- The HTTP client under `ITallyXmlClient` has a **read-only transport** Polly pipeline (2 retries, ~200 ms jittered backoff, triggered on `HttpRequestException` + 5xx) for safe master/health reads. Voucher writes are marked and excluded from that pipeline, so one operator action makes at most one voucher HTTP attempt. Tally body errors (`LINEERROR`, `EXCEPTIONS > 0`) with no created/altered record flow straight to `failed`; with a created/altered record they flow to `reconciliation_required`.
- Do not re-introduce a queue, polling loop, or second process as an "optimization." The architecture was explicitly collapsed from two-process to single-process; reversing it would re-open the whole class of bugs we removed.
