# Tally XML Golden Path

**Status:** Canonical reference for the live Tally XML read and write paths  
**Last live verification:**
- write path (sales voucher, Tally Wrapper C# builder): April 23, 2026
- read path (master collections): April 20, 2026

**Verified against:** `192.168.1.13:9000`  
**Verified company:** `dummy`

## Change control

Do not change the accepted XML shape or the `REMOTEID` handling in this document without live revalidation against a real Tally company.

---

## 1. Purpose

Use this document as the single place to answer two questions:

1. what is the correct XML posting path for sales vouchers in this system
2. what has already been learned from live Tally behavior

If sales vouchers stop posting, start here before changing the XML builder or posting logic.

---

## 2. Canonical code path

### 2.1 Public write entrypoint

All live sales-voucher writes go through the API's in-process Tally integration:

- `ITallyPoster.PostAsync` → `TallyXmlVoucherBuilder.Build` → `ITallyXmlClient.SendWriteAsync`
- all three live under `src/ShowroomBilling.Infrastructure/Tally/`
- invoked synchronously by `BillPostingWorkflow.PushInternalAsync` when the operator clicks Push / Retry / Repost
- there is no separate bridge process and no posting queue

### 2.2 One attempt per operator action

A push, retry or repost builds one voucher envelope (§4.3) and sends it once. There is no second XML shape and no fallback request: Tally's answer, or the lack of one, settles the bill (§4.5).

The builder emits one layout, the Accounting Invoice shape: `ALLLEDGERENTRIES.LIST` entries, with the bill lines as `INVENTORYALLOCATIONS.LIST` inside the sales entry. It emits no `BATCHALLOCATIONS.LIST`.

---

## 3. Masters read path

Implemented in `TallyXmlClient` + `TallyXmlMasterSource` under `src/ShowroomBilling.Infrastructure/Tally/`.

### 3.1 Transport

- POST directly to the Tally HTTP endpoint root (`http://<host>:<port>/`). No path segment — Tally does not route by URL.
- `Content-Type: text/xml; charset=utf-8`. Tally also accepts `application/xml`.
- Expected response: `HTTP 200` with an `<ENVELOPE>` body. Non-200 is a hard error; status header `<STATUS>1</STATUS>` inside the envelope indicates success at the Tally layer.

### 3.2 Request shape (collection exports)

Every master fetch is a `TYPE=Collection` export request. Canonical template (companies):

```xml
<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Collection</TYPE>
    <ID>TW_Companies</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
        <!-- SVCURRENTCOMPANY is required for every master EXCEPT the company list -->
      </STATICVARIABLES>
      <TDL>
        <TDLMESSAGE>
          <COLLECTION NAME="TW_Companies">
            <TYPE>Company</TYPE>
            <FETCH>Name, IsInactive</FETCH>
          </COLLECTION>
        </TDLMESSAGE>
      </TDL>
    </DESC>
  </BODY>
</ENVELOPE>
```

Findings from live validation:

- Title-case request verbs (`Export`, `Collection`) work — Tally also accepts the uppercase form (`EXPORT`, `COLLECTION`). The client sends title case.
- `<COLLECTION>` type names with a space (`Stock Item`, `Voucher Type`) work. The unspaced forms (`StockItem`, `VoucherType`) are accepted too.
- `<FETCH>Name, Field1, Field2</FETCH>` is a comma-separated single element. Repeated `<NATIVEMETHOD>` elements, one per field, give the same result set for master collections.
- **Do not scope the company-list request** with `<SVCURRENTCOMPANY>`. Tally returns the list of all companies currently open in the server; scoping it prevents the outer envelope from enumerating other companies.
- Every other master fetch **must** include `<SVCURRENTCOMPANY>...</SVCURRENTCOMPANY>` inside `<STATICVARIABLES>`, otherwise Tally returns zero rows.

### 3.3 Fetch field lists (what we ask for per master)

| Master | TYPE | FETCH fields |
|---|---|---|
| Companies | `Company` | `Name, IsInactive` |
| Ledgers | `Ledger` | `Name, Parent, PrimaryGroup, IsRevenue, PartyGSTIN` |
| Stock items | `Stock Item` | `Name, AliasName, BaseUnits, GSTHSNCode` |
| Voucher types | `Voucher Type` | `Name, Parent, IsDeemedPositive` |

Tally may silently drop requested fields from the response (e.g. the `dummy` company returned ledgers without `<PRIMARYGROUP>` even though we asked for it, and returned companies without `<ISINACTIVE>`). Parsers must treat every field except `Name` as optional.

### 3.4 Response shape

The response wraps payload elements inside `<BODY><DESC><CMPINFO>...</CMPINFO></DESC><DATA><COLLECTION>...</COLLECTION></DATA></BODY>`. It is not the `<TALLYMESSAGE>` wrapper that voucher imports use. Parsers must use a local-name descendant search rather than fixed path matching so they work for both shapes.

Example abbreviated ledger response:

```xml
<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <STATUS>1</STATUS>
  </HEADER>
  <BODY>
    <DESC><CMPINFO>...</CMPINFO></DESC>
    <DATA>
      <COLLECTION ISMSTDEPTYPE="Yes" MSTDEPTYPE="8">
        <LEDGER NAME="AARAV JEWELS" RESERVEDNAME="">
          <PARENT TYPE="String">Sundry Debtors</PARENT>
          <PARTYGSTIN TYPE="String">09CHJPR2676H1ZR</PARTYGSTIN>
          <ISREVENUE TYPE="Logical">No</ISREVENUE>
          <LANGUAGENAME.LIST>
            <NAME.LIST TYPE="String"><NAME>AARAV JEWELS</NAME></NAME.LIST>
            <LANGUAGEID TYPE="Number"> 1033</LANGUAGEID>
          </LANGUAGENAME.LIST>
        </LEDGER>
      </COLLECTION>
    </DATA>
  </BODY>
</ENVELOPE>
```

Quirks the parser must handle:

- the object name is on the attribute (`NAME="..."`) AND duplicated as nested `<LANGUAGENAME.LIST><NAME.LIST><NAME>`. Prefer the attribute; fall back to the first child `<NAME>` only when the attribute is empty.
- `<LANGUAGEID>` text starts with a leading space (`" 1033"`). Any child-value reader must `.Trim()`.
- every scalar child carries a `TYPE="String|Logical|Number"` attribute. The value is still in element text; do not try to cast by attribute.
- `<ISREVENUE>`, `<ISDEEMEDPOSITIVE>`, `<ISINACTIVE>` are `Yes`/`No`. Tally also uses `True`/`False` and sometimes `1`/`0` elsewhere; accept all three.

### 3.5 Invalid XML 1.0 characters (critical)

**Tally emits invalid XML 1.0 characters in two forms.** Observed cases:

- **Raw C0 bytes** in text content (seen intermittently).
- **Numeric character references** like `&#4;` in text content. For the `dummy` company, every `<STOCKITEM>` has `<GSTHSNCODE>&#4; Not Found</GSTHSNCODE>`. `&#4;` resolves to `U+0004` (`EOT`), which is not a valid XML 1.0 character. `.NET`'s `XmlReader` rejects the reference during entity decoding with `XmlException: hexadecimal value 0x04, is an invalid character`, even though the reference itself is syntactically legal.

Mitigation (`TallyXmlClient.StripInvalidXmlControlChars`):

- before `XElement.Parse`, regex-strip raw `[\x00-\x08\x0B\x0C\x0E-\x1F]` bytes from the response body. Tab (`\x09`), LF (`\x0A`), CR (`\x0D`) are preserved.
- then regex-match every `&#<decimal>;` / `&#x<hex>;` in the body; drop the reference when the code point is invalid for XML 1.0 (C0 except tab/LF/CR, and C1 except NEL `U+0085`). Valid references (e.g. `&#65;` for `A`) pass through unchanged.

### 3.6 Sentinel values (treat as missing)

Tally reports "no value set" for some master fields with sentinel strings rather than omitting the element:

| Field | Sentinel | Treat as |
|---|---|---|
| `<GSTHSNCODE>` | `"\x04 Not Found"` → after strip: `"Not Found"` | `null` HSN; fall through to `<HSNCODE>` if present |

Implemented in `TallyXmlMasterSource.SanitizeHsn`. Add new sentinels here as they are discovered from live data.

### 3.7 Live counts observed (April 20, 2026)

Against `http://192.168.1.13:9000`, company `dummy`:

- companies: 2 (`DEEN DAYAL ANAND KUMAR SARRAF`, `dummy`)
- ledgers: 397
- stock items: 11 (all HSNs returned as the `\x04 Not Found` sentinel)
- voucher types: 33 (includes legacy `Counter Sale` → parent `Sales`)

---

## 4. Voucher write path

Section 3 documents the **read** path (masters). This section documents the **write** path: how the API posts a bill to Tally as a sales voucher.

### 4.1 Pipeline

Synchronous, in-process, driven by an operator click on Push / Retry / Repost:

1. `BillPostingWorkflow.PushInternalAsync` transitions the bill to `posting` and saves (so a crash is recoverable).
2. Builds a `TallyPostRequest` carrying the bill header + `BillPayloadDto` from the current revision's snapshot. `Operation=Create` is the default; `Operation=Alter` is used only for `EditedAfterPush=true` bills after resolving the old Tally `MASTER ID` from pre-edit audit.
3. Calls `ITallyPoster.PostAsync` which:
   - reads ledger mappings + active company from cloud settings via `ICloudSettingsService`
   - builds an Import Data envelope via `TallyXmlVoucherBuilder.Build`
   - sends it through `ITallyXmlClient.SendWriteAsync` (one HTTP POST to the Tally endpoint in Settings; never retried)
   - classifies the response
4. `BillPostingWorkflow` records `tally.posted`, `tally.failed` or `tally.outcome.unknown`, transitions the bill to `posted`, `failed` or `reconciliation_required`, saves, and returns the updated `BillResponse` to the desktop. Successful alter clears `EditedAfterPush` and stores the target `tallyMasterId`; failed alter keeps the old audit target for retry.

The desktop's Push button stays busy for the full duration of one Tally round-trip (typically 1–10 seconds).

### 4.2 Required cloud-settings ledger mappings

`TallyPoster` reads these from `ICloudSettingsService.GetEffectiveSettingsAsync` on every call and hands them to the builder (the API keeps settings for at most five seconds; a save through the same API clears them at once):

| Cloud-settings field | Purpose | Required |
|---|---|---|
| `Ledgers.SalesVoucherType` | `<VOUCHERTYPENAME>` + `VCHTYPE` attribute on `<VOUCHER>` | always (defaults to `Sales`) |
| `Ledgers.SalesLedger` | `<LEDGERNAME>` of the sales `ALLLEDGERENTRIES.LIST` entry, which holds the inventory lines | always |
| `Ledgers.CashLedger` | `<PARTYLEDGERNAME>`, `<PARTYNAME>`, and offsetting party Dr ledger when `bill.Payment` normalizes to `Cash` | for a `Cash` bill |
| `Ledgers.CreditDebitLedger` | `<PARTYLEDGERNAME>`, `<PARTYNAME>`, and offsetting party Dr ledger when `bill.Payment` normalizes to `Credit and debit` | for every other bill |
| Unregistered/consumer sales policy | `<GSTREGISTRATIONTYPE>Unregistered/Consumer</GSTREGISTRATIONTYPE>`; no `<PARTYGSTIN>` / `<CONSIGNEEGSTIN>` is emitted by this app | always |
| `Ledgers.CgstLedger` + `Ledgers.SgstLedger` | Credit ledgers for the CGST / SGST halves of `BillTotals.TaxTotal` (`JewelleryGst.Split`, the split the printed invoice uses) | each only when its half is not zero |
| `Ledgers.DiscountLedger` | Debit ledger for `BillTotals.DiscountTotal` | only when `DiscountTotal != 0` |
| `Ledgers.RoundOffLedger` | Credit/debit ledger for `BillTotals.RoundOff` | only when `RoundOff != 0` |
| `Connection.ActiveCompanyName` | `<SVCURRENTCOMPANY>` scope for the import | always |
| `Print.CompanyState` | `<STATENAME>`, `<PLACEOFSUPPLY>`, `<CONSIGNEESTATENAME>` GST transaction state fields | when configured |
| `Print.CompanyCountry` | `<COUNTRYNAME>` for the voucher view plus `<COUNTRYOFRESIDENCE>` / `<CONSIGNEECOUNTRYNAME>` for GST/GSTR-1 fields | when configured |

A missing mapping raises `VoucherBuildException` before anything is sent, surfacing in the UI as a `failed` bill with a `CONFIG_MISSING_*` error code — the operator fixes the mapping in Settings, then clicks Retry. The builder also checks that the ledger amounts net to zero exactly as they will be sent, and fails with `VOUCHER_UNBALANCED` if they do not.

The `{PartyLedger}` placeholder in `<PARTYLEDGERNAME>` and the offsetting Dr ledger entry is **resolved from the bill's payment mode**, not from the customer name. `bill.Payment` is normalized via [`PaymentMode.Normalize`](../src/ShowroomBilling.Contracts/Bills/PaymentMode.cs) into `Cash` or `Credit and debit`, then mapped to `Ledgers.CashLedger` or `Ledgers.CreditDebitLedger`. The free-text `bill.PartyName` (operator's customer label) is joined with `bill.Notes` and emitted as `<NARRATION>` — it never reaches `<PARTYLEDGERNAME>`. See [`05_tally_integration_contract.md` §3.1](05_tally_integration_contract.md) for the full derivation rule.

`<PARTYNAME>` deliberately uses the same resolved party ledger. It is a Tally GST transaction field, not the free-text customer label.

### 4.3 Envelope shape

This is the *Accounting Invoice* layout that `TallyXmlVoucherBuilder.Build` produces, live-verified against the `dummy` company. Amounts are written with two decimals. `VOUCHERNUMBER`, the three state elements, the three country elements and `NARRATION` are left out when they have no value.

```xml
<ENVELOPE>
  <HEADER>
    <TALLYREQUEST>Import Data</TALLYREQUEST>
  </HEADER>
  <BODY>
    <IMPORTDATA>
      <REQUESTDESC>
        <REPORTNAME>Vouchers</REPORTNAME>
        <STATICVARIABLES>
          <SVCURRENTCOMPANY>{company}</SVCURRENTCOMPANY>
        </STATICVARIABLES>
      </REQUESTDESC>
      <REQUESTDATA>
        <TALLYMESSAGE xmlns:UDF="TallyUDF">
          <VOUCHER REMOTEID="{IdempotencyKey}" VCHTYPE="{SalesVoucherType}" ACTION="Create">
            <DATE>{yyyyMMdd}</DATE>
            <EFFECTIVEDATE>{yyyyMMdd}</EFFECTIVEDATE>
            <VOUCHERTYPENAME>{SalesVoucherType}</VOUCHERTYPENAME>
            <VOUCHERNUMBER>{InvoiceNumber}</VOUCHERNUMBER>
            <PARTYLEDGERNAME>{PartyLedger}</PARTYLEDGERNAME>
            <PARTYNAME>{PartyLedger}</PARTYNAME>
            <GSTREGISTRATIONTYPE>Unregistered/Consumer</GSTREGISTRATIONTYPE>
            <STATENAME>{CompanyState}</STATENAME>
            <PLACEOFSUPPLY>{CompanyState}</PLACEOFSUPPLY>
            <CONSIGNEESTATENAME>{CompanyState}</CONSIGNEESTATENAME>
            <COUNTRYNAME>{CompanyCountry}</COUNTRYNAME>
            <COUNTRYOFRESIDENCE>{CompanyCountry}</COUNTRYOFRESIDENCE>
            <CONSIGNEECOUNTRYNAME>{CompanyCountry}</CONSIGNEECOUNTRYNAME>
            <ISINVOICE>Yes</ISINVOICE>
            <NARRATION>{PartyName} | {Notes}</NARRATION>

            <!-- 1. Party (Dr): AMOUNT is NEGATIVE; ISDEEMEDPOSITIVE=Yes for Dr-natured ledgers -->
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>{PartyLedger}</LEDGERNAME>
              <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
              <ISPARTYLEDGER>Yes</ISPARTYLEDGER>
              <AMOUNT>-{GrandTotal}</AMOUNT>
            </ALLLEDGERENTRIES.LIST>

            <!-- 2. Sales (Cr): AMOUNT is POSITIVE = net Subtotal. Inventory nests INSIDE this entry. -->
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>{SalesLedger}</LEDGERNAME>
              <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              <AMOUNT>{Subtotal}</AMOUNT>
              <!-- Per bill line; AMOUNT here is the line's NET share (gross × Subtotal ÷ ΣLineTotal) -->
              <INVENTORYALLOCATIONS.LIST>
                <STOCKITEMNAME>{StockName}</STOCKITEMNAME>
                <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
                <RATE>{netAmount/qty}/{unit}</RATE>
                <AMOUNT>{netAmount}</AMOUNT>
                <ACTUALQTY>{qty} {unit}</ACTUALQTY>
                <BILLEDQTY>{qty} {unit}</BILLEDQTY>
              </INVENTORYALLOCATIONS.LIST>
            </ALLLEDGERENTRIES.LIST>

            <!-- 3. Tax (Cr): AMOUNT is POSITIVE -->
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>{CgstLedger}</LEDGERNAME>
              <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              <AMOUNT>{cgst}</AMOUNT>
            </ALLLEDGERENTRIES.LIST>
            <!-- SGST: same as CGST -->

            <!-- 4. Discount (Dr contra): AMOUNT is NEGATIVE (only if DiscountTotal > 0) -->
            <!-- 5. Round-off: AMOUNT preserves sign (positive = Cr income; negative = Dr expense) -->
          </VOUCHER>
        </TALLYMESSAGE>
      </REQUESTDATA>
    </IMPORTDATA>
  </BODY>
</ENVELOPE>
```

For an edited-after-push bill, the same voucher body is emitted with alter attributes instead of a create `REMOTEID`:

```xml
<VOUCHER VCHTYPE="{SalesVoucherType}" ACTION="Alter" TAGNAME="MASTER ID" TAGVALUE="{oldTallyMasterId}">
  <DATE>{yyyyMMdd}</DATE>
  <EFFECTIVEDATE>{yyyyMMdd}</EFFECTIVEDATE>
  <VOUCHERTYPENAME>{SalesVoucherType}</VOUCHERTYPENAME>
  <VOUCHERNUMBER>{InvoiceNumber}</VOUCHERNUMBER>
  ...
</VOUCHER>
```

The API resolves `{oldTallyMasterId}` from the last successful pre-edit `tally.posted` audit, preferring `details.tallyMasterId` and falling back to numeric legacy `details.remoteId`. If that target is missing, it records `TALLY_ALTER_TARGET_MISSING` and does not call Tally or create a replacement voucher.

**Sign convention (Accounting Invoice view).** Tally's voucher import interprets the `AMOUNT` sign as the net change from the company's Cr-side perspective, not directly as Dr/Cr:

- **Dr legs** (Cash/party receiving, Discount expense) → **negative** `AMOUNT`
- **Cr legs** (Sales revenue, CGST, SGST, positive Round-off income) → **positive** `AMOUNT`
- **Round-off** preserves sign: `+0.01` posts Cr 0.01 (income); `-0.01` posts Dr 0.01 (expense)
- **Inventory `AMOUNT` matches its parent sales ledger entry** (positive for a Cr sale). Per-line amounts are the *net* share of Subtotal, not the gross `LineTotal` — otherwise the voucher double-counts tax and fails to balance.

The builder uses proportional allocation so multi-line bills with rounding drift still sum exactly to `bill.Totals.Subtotal`: each line gets `Math.Round(LineTotal / ΣLineTotal × Subtotal, 2)`, with any residual absorbed by the last line.

#### 4.3.1 Gotchas that produce silent `TALLY_NO_EFFECT`

These are all bugs that caused real failed pushes during Tally Wrapper bring-up — each one produces the opaque response `<RESPONSE>Unknown Request, cannot be processed</RESPONSE>` (with `CREATED=0, ALTERED=0, ERRORS=0, EXCEPTIONS=0`), which `TallyPoster` classifies as `TALLY_NO_EFFECT`. None of them produce a useful LINEERROR or ERRORS count, so you *must* inspect the `requestExcerpt` and `responseExcerpt` on the `tally.failed` audit event.

1. **`<VERSION>1</VERSION>` in the Import Data header.** Tally Prime's Import Data handler rejects the request outright when `VERSION` is present. The Export request shape *requires* `<VERSION>`, which tempts copy-paste; Import requests *must* omit it. This was the single biggest blocker during Tally Wrapper bring-up.
2. **Flat `ALLINVENTORYENTRIES.LIST` + `ACCOUNTINGALLOCATIONS.LIST` layout.** This is the Item-Invoice shape — Tally accepts it *on some companies* but returned `EXCEPTIONS=1` against `dummy`. The nested `INVENTORYALLOCATIONS.LIST` layout shown above is the canonical one.
3. **Sign convention inverted** (party positive, sales negative). Looks plausible if you think of `AMOUNT` as a signed Dr/Cr value, but produces `EXCEPTIONS=1`. Use the Dr-negative / Cr-positive convention above.
4. **Sales allocation uses gross `LineTotal` instead of net.** `lineTotal` in the bill payload is the gross (tax-inclusive) value. Posting it directly to Sales while *also* posting CGST + SGST separately double-counts tax, leaves the voucher imbalanced by exactly `TaxTotal`, and Tally rejects it silently.
5. **Ledger name mismatch between cloud-settings and Tally company.** `CONFIG_MISSING_CASH_LEDGER` / `CONFIG_MISSING_CREDIT_DEBIT_LEDGER` / `MISSING_PAYMENT_MODE` are caught pre-send, but every *other* ledger (CGST/SGST/RoundOff/Sales/Discount) is only validated by Tally itself — and a missing ledger here surfaces as the same `Unknown Request` response, never as a LINEERROR naming the offender. Use `List of Ledgers` via a Tally collection export to verify each name exists in the target company before debugging the XML.
6. **`ActiveCompanyName` doesn't match a company open in Tally.** Same symptom — request accepted, 0 created, 0 altered, no error. Verify with the `$$CurrentCompany` function export before blaming the voucher XML.

### 4.4 `REMOTEID` idempotency

`REMOTEID` on create imports is `post:{billId:N}:{revisionId:N}`; an explicit repost sends the caller's own key instead. Tally enforces uniqueness on this field per company, so if a push happens twice against the same bill revision (e.g. network glitch, retry), Tally rejects the second import with `ERRORS > 0` — classified as `TALLY_ERRORS`, landing the bill in `failed`. The operator checks Tally and then decides: Mark as Pushed (admin, local-only) when Tally holds the voucher, or Retry (same `REMOTEID`; Tally rejects it again while it holds the first voucher). An edit saves a new revision, so the next push carries a fresh `REMOTEID`; if Tally still holds the first voucher that creates a second one.

Live finding (April 11, 2026): after an import that failed with `EXCEPTIONS=1`, a second, otherwise valid import that reused the same `REMOTEID` failed too, and one with a fresh `REMOTEID` succeeded.

Edited-after-push alter requests do not send `REMOTEID`; they target the old voucher with `TAGNAME="MASTER ID"` + `TAGVALUE="{oldTallyMasterId}"`.

### 4.5 Response classification

Tally's answer to a create it accepted (captured live):

```xml
<RESPONSE>
  <CREATED>1</CREATED>
  <ALTERED>0</ALTERED>
  <DELETED>0</DELETED>
  <LASTVCHID>16945</LASTVCHID>
  <LASTMID>0</LASTMID>
  <COMBINED>0</COMBINED>
  <IGNORED>0</IGNORED>
  <ERRORS>0</ERRORS>
  <CANCELLED>0</CANCELLED>
  <EXCEPTIONS>0</EXCEPTIONS>
</RESPONSE>
```

`TallyPoster.ClassifyResponse`, in order:

| Condition | Outcome | Error code |
|---|---|---|
| a `<LINEERROR>`, `ERRORS > 0` or `EXCEPTIONS > 0` **and** `CREATED + ALTERED > 0` | `Unknown` | `TALLY_PARTIAL_EFFECT` |
| any `<LINEERROR>` descendant | `Failed` | `TALLY_LINEERROR` |
| `<ERRORS>` > 0 or `<EXCEPTIONS>` > 0 | `Failed` | `TALLY_ERRORS` |
| alter request and `CREATED > 0` | `Unknown` | `TALLY_UNEXPECTED_CREATE_ON_ALTER` |
| create request and `CREATED + ALTERED <= 0` | `Failed` | `TALLY_NO_EFFECT` |
| alter request and `ALTERED <= 0` | `Failed` | `TALLY_NO_EFFECT` |
| otherwise | `Posted` | — |

`RemoteId` on create success: `LASTVCHID ?? LASTMID ?? request.IdempotencyKey`. `tallyMasterId` is captured from positive numeric `LASTVCHID`.

`RemoteId` on alter success: `LASTVCHID ?? LASTMID ?? request.TargetTagValue`; `tallyMasterId` is `LASTVCHID` when positive numeric, otherwise the alter target.

Transport failures after a voucher write begins are classified as `Unknown`: `TALLY_TRANSPORT_UNKNOWN` for HTTP/transport errors, `TALLY_TIMEOUT` for the configured timeout, and `TALLY_RESPONSE_UNKNOWN` for an unusable response. They move the bill to `reconciliation_required`. Missing configuration is known before sending and remains `Failed` with `TALLY_NOT_CONFIGURED`. Any other failure while loading settings or building/serializing the voucher XML happens before a byte is sent, so it is `Failed` with `TALLY_PREPARE_FAILED` (and `BillValidator` rejects XML-invalid control characters in text fields at save time). A response that changed something in Tally while also reporting an error is never a definite failure: Retry/Repost from `failed` would duplicate the voucher. `BillPostingStatusResponse` returns the latest problem's `errorCode` + `errorMessage`.

The `tally.failed` and `tally.outcome.unknown` audit events carry truncated `requestExcerpt` and `responseExcerpt` (≤4000 chars each) for diagnosis.

### 4.6 What the operator does next

There is no auto-retry: the operator decides what to do next. A `failed` bill cannot be revised; it is edited (which returns it to `pending`) or retried.

- **Config errors → fix Settings, then Retry:** `CONFIG_MISSING_SALES_LEDGER`, `CONFIG_MISSING_CASH_LEDGER`, `CONFIG_MISSING_CREDIT_DEBIT_LEDGER`, `CONFIG_MISSING_CGST_LEDGER`, `CONFIG_MISSING_SGST_LEDGER`, `CONFIG_MISSING_ROUNDOFF_LEDGER`, `CONFIG_MISSING_DISCOUNT_LEDGER`, `CONFIG_MISSING_COMPANY`, `TALLY_NOT_CONFIGURED`.
- **Bill-content errors → Edit, then Push:** `MISSING_PAYMENT_MODE`, `NO_LINES`, `VOUCHER_UNBALANCED`.
- **Ambiguous write outcomes → verify Tally, then resolve:** `TALLY_TRANSPORT_UNKNOWN`, `TALLY_TIMEOUT`, `TALLY_RESPONSE_UNKNOWN`, `TALLY_PARTIAL_EFFECT`, `TALLY_UNEXPECTED_CREATE_ON_ALTER`, `TALLY_UNEXPECTED`.
- **Definite Tally rejection → correct data/config, then Retry or Edit:** `TALLY_LINEERROR`, `TALLY_ERRORS`, `TALLY_NO_EFFECT`, `TALLY_PREPARE_FAILED`.
- **Edited bill whose original voucher id cannot be found:** `TALLY_ALTER_TARGET_MISSING`. Nothing is sent to Tally and no replacement voucher is created.

Every voucher click makes one HTTP attempt — there is no automatic voucher retry, backoff, or next-attempt clock. Safe XML reads retain the short transport retry pipeline.

---

## 5. Verification command

Current focused regression command:

```powershell
dotnet test --project tests/ShowroomBilling.Tests/ShowroomBilling.Tests.csproj --filter "FullyQualifiedName~TallyPosterTests|FullyQualifiedName~TallyXmlRetryPolicyTests|FullyQualifiedName~TallyXmlVoucherBuilderTests"
```
