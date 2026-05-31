# Tally XML Golden Path

**Status:** Canonical reference for the live Tally XML read and write paths  
**Last live verification:**
- write path (sales voucher, V2 C# builder): April 23, 2026
- read path (master collections): April 20, 2026

**Verified against:** `192.168.1.13:9000`  
**Verified company:** `dummy`

## Change control

Do not change the posting order, accepted XML shape, fallback sequence, or `REMOTEID` handling in this document without live revalidation against a real Tally company. This is the golden path for V2 as well.

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

- `ITallyPoster.PostAsync` → `TallyXmlVoucherBuilder.Build` → `ITallyXmlClient.SendAsync`
- all three live under `src/ShowroomBilling.Infrastructure/Tally/`
- invoked synchronously by `BillService.PushInternalAsync` when the operator clicks Push / Retry / Repost
- there is no separate bridge process and no posting queue

### 2.2 Canonical posting order

The canonical live write path is:

1. build **plain** sales-voucher XML first
2. send plain XML with `include_batch_allocations=True`
3. if Tally rejects that import, retry once with plain XML and `include_batch_allocations=False`
4. do not try `item_invoice` first

Canonical builder sequence:

1. `build_sales_voucher_xml(..., shape="plain", include_batch_allocations=True)`
2. fallback once to `build_sales_voucher_xml(..., shape="plain", include_batch_allocations=False)`

### 2.3 Do not use this as the first live write path

Do **not** use `shape="item_invoice"` as the first live posting shape unless it has been revalidated against the target company.

Reason:

- on April 11, 2026, `item_invoice` failed on `dummy`
- retrying plain XML with the same `REMOTEID` after that failure also failed
- plain XML succeeds when sent first

---

## 3. Working XML strategy

### 3.1 Primary accepted shape

Primary accepted shape:

- `shape="plain"`
- `include_batch_allocations=True`

This produces:

- `ALLLEDGERENTRIES.LIST`
- nested `INVENTORYALLOCATIONS.LIST`
- optional `BATCHALLOCATIONS.LIST`

### 3.2 Compatibility fallback shape

Fallback shape:

- `shape="plain"`
- `include_batch_allocations=False`

This exists because some Tally/company combinations may silently reject batch allocations.

### 3.3 Why the plain path is canonical

Live finding from April 11, 2026:

- `item_invoice` returned `EXCEPTIONS=1`
- plain XML posted successfully when it was sent first
- the plain XML retry could fail if it reused the same `REMOTEID` after an `item_invoice` failure

Practical rule:

- treat plain XML as the source-of-truth posting path
- treat invoice-view XML as experimental unless reverified

---

## 4. `REMOTEID` behavior warning

Live finding:

1. `item_invoice` with `REMOTEID = X` failed
2. plain XML with the same `REMOTEID = X` also failed
3. plain XML with a fresh `REMOTEID = Y` succeeded

Rules:

- do not change shape after a failed write while blindly reusing the same `REMOTEID`
- if a fallback materially changes the XML path, issue a fresh `REMOTEID`
- keep cloud idempotency separate from Tally `REMOTEID`
- use reconciliation before retrying ambiguous outcomes

---

## 5. Live-verified accepted request

This request shape was accepted by Tally during live verification on April 11, 2026.

Voucher details used:

- company: `dummy`
- date: `2026-03-22`
- party ledger: `CASH`
- stock item: `Halmarked Gold Jewellery 22KT`
- quantity: `0.010 GMS`
- subtotal: `100.000`
- CGST: `1.500`
- SGST: `1.500`
- grand total: `103.000`

Accepted request:

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
          <SVCURRENTCOMPANY>dummy</SVCURRENTCOMPANY>
        </STATICVARIABLES>
      </REQUESTDESC>
      <REQUESTDATA>
        <TALLYMESSAGE xmlns:UDF="TallyUDF">
          <VOUCHER REMOTEID="codex-live-fixed-bb6cff4c-dd19-498e-bbf4-f719b4fc615b" VCHTYPE="Sales" ACTION="Create">
            <DATE>20260322</DATE>
            <NARRATION>Cash Sale Test | Codex live fixed codex-live-fixed-bb6cff4c-dd19-498e-bbf4-f719b4fc615b</NARRATION>
            <VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>
            <ISINVOICE>Yes</ISINVOICE>
            <PARTYLEDGERNAME>CASH</PARTYLEDGERNAME>
            <STATENAME>Uttar Pradesh</STATENAME>
            <PLACEOFSUPPLY>Uttar Pradesh</PLACEOFSUPPLY>
            <COUNTRYNAME>India</COUNTRYNAME>
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>CASH</LEDGERNAME>
              <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
              <AMOUNT>-103.000</AMOUNT>
            </ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>SALES GOLD JEWELLERY</LEDGERNAME>
              <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              <AMOUNT>100.000</AMOUNT>
              <INVENTORYALLOCATIONS.LIST>
                <STOCKITEMNAME>Halmarked Gold Jewellery 22KT</STOCKITEMNAME>
                <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
                <RATE>10000.000/GMS</RATE>
                <AMOUNT>100.000</AMOUNT>
                <ACTUALQTY>0.010 GMS</ACTUALQTY>
                <BILLEDQTY>0.010 GMS</BILLEDQTY>
                <BATCHALLOCATIONS.LIST>
                  <GODOWNNAME>Main Location</GODOWNNAME>
                  <BATCHNAME>Primary Batch</BATCHNAME>
                  <AMOUNT>100.000</AMOUNT>
                  <ACTUALQTY>0.010 GMS</ACTUALQTY>
                  <BILLEDQTY>0.010 GMS</BILLEDQTY>
                </BATCHALLOCATIONS.LIST>
              </INVENTORYALLOCATIONS.LIST>
            </ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>CGST TAX</LEDGERNAME>
              <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              <AMOUNT>1.500</AMOUNT>
            </ALLLEDGERENTRIES.LIST>
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>SGST TAX</LEDGERNAME>
              <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
              <AMOUNT>1.500</AMOUNT>
            </ALLLEDGERENTRIES.LIST>
          </VOUCHER>
        </TALLYMESSAGE>
      </REQUESTDATA>
    </IMPORTDATA>
  </BODY>
</ENVELOPE>
```

Accepted response:

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

Captured artifacts for that successful post:

- `tmp/live_validation/codex-live-fixed-bb6cff4c-dd19-498e-bbf4-f719b4fc615b.plain.request.xml`
- `tmp/live_validation/codex-live-fixed-bb6cff4c-dd19-498e-bbf4-f719b4fc615b.response.xml`

---

## 6. Debug sequence

When vouchers are not posting, use this order:

1. probe Tally reachability and open company visibility
2. fetch voucher types, ledgers, and stock items from the target company
3. confirm the configured ledgers match Tally exactly
4. inspect the XML generated by `build_sales_voucher_xml(..., shape="plain", include_batch_allocations=True)`
5. post the plain XML first
6. if rejected, try the plain no-batch fallback
7. only experiment with `item_invoice` after the plain path has been ruled out

Useful helper:

- `tools/tally_live_validation_helper.py`

---

## 7. Masters read path

**Status as of April 20, 2026:** implemented in `TallyXmlClient` + `TallyXmlMasterSource` in the API's Infrastructure layer. Replaces the Python V1 `tally/xml_builder.py` + `tally/xml_parser.py`.

### 7.1 Transport

- POST directly to the Tally HTTP endpoint root (`http://<host>:<port>/`). No path segment — Tally does not route by URL.
- `Content-Type: text/xml; charset=utf-8`. Tally also accepts `application/xml` but `text/xml` matches V1 and observed real-world Tally examples.
- Expected response: `HTTP 200` with an `<ENVELOPE>` body. Non-200 is a hard error; status header `<STATUS>1</STATUS>` inside the envelope indicates success at the Tally layer.

### 7.2 Request shape (collection exports)

Every master fetch is a `TYPE=Collection` export request. Canonical template (companies):

```xml
<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Collection</TYPE>
    <ID>SBV2_Companies</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
        <!-- SVCURRENTCOMPANY is required for every master EXCEPT the company list -->
      </STATICVARIABLES>
      <TDL>
        <TDLMESSAGE>
          <COLLECTION NAME="SBV2_Companies">
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

- Title-case request verbs (`Export`, `Collection`) work — Tally also accepts the uppercase V1 form (`EXPORT`, `COLLECTION`). We standardized on title case.
- `<COLLECTION>` type names with a space (`Stock Item`, `Voucher Type`) work. V1 used the unspaced forms (`StockItem`, `VoucherType`); both are accepted.
- `<FETCH>Name, Field1, Field2</FETCH>` is a comma-separated single element. V1 used repeated `<NATIVEMETHOD>` elements per field; `<FETCH>` is equivalent for master collections and produces the same result set.
- **Do not scope the company-list request** with `<SVCURRENTCOMPANY>`. Tally returns the list of all companies currently open in the server; scoping it prevents the outer envelope from enumerating other companies.
- Every other master fetch **must** include `<SVCURRENTCOMPANY>...</SVCURRENTCOMPANY>` inside `<STATICVARIABLES>`, otherwise Tally returns zero rows.

### 7.3 Fetch field lists (what we ask for per master)

| Master | TYPE | FETCH fields |
|---|---|---|
| Companies | `Company` | `Name, IsInactive` |
| Ledgers | `Ledger` | `Name, Parent, PrimaryGroup, IsRevenue, PartyGSTIN` |
| Stock items | `Stock Item` | `Name, AliasName, BaseUnits, GSTHSNCode` |
| Voucher types | `Voucher Type` | `Name, Parent, IsDeemedPositive` |

Tally may silently drop requested fields from the response (e.g. the `dummy` company returned ledgers without `<PRIMARYGROUP>` even though we asked for it, and returned companies without `<ISINACTIVE>`). Parsers must treat every field except `Name` as optional.

### 7.4 Response shape

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

### 7.5 Invalid XML 1.0 characters (critical)

**Tally emits invalid XML 1.0 characters in two forms.** Observed cases:

- **Raw C0 bytes** in text content (seen intermittently; V1 strips these defensively).
- **Numeric character references** like `&#4;` in text content. For the `dummy` company, every `<STOCKITEM>` has `<GSTHSNCODE>&#4; Not Found</GSTHSNCODE>`. `&#4;` resolves to `U+0004` (`EOT`), which is not a valid XML 1.0 character. `.NET`'s `XmlReader` rejects the reference during entity decoding with `XmlException: hexadecimal value 0x04, is an invalid character`, even though the reference itself is syntactically legal.

Mitigation (`TallyXmlClient.StripInvalidXmlControlChars`):

- before `XElement.Parse`, regex-strip raw `[\x00-\x08\x0B\x0C\x0E-\x1F]` bytes from the response body. Tab (`\x09`), LF (`\x0A`), CR (`\x0D`) are preserved.
- then regex-match every `&#<decimal>;` / `&#x<hex>;` in the body; drop the reference when the code point is invalid for XML 1.0 (C0 except tab/LF/CR, and C1 except NEL `U+0085`). Valid references (e.g. `&#65;` for `A`) pass through unchanged.
- this matches V1's behavior: V1 strips C0 raw bytes via `CONTROL_CHARS_RE` (`tally/xml_parser.py:strip_control_chars`) and happens to avoid the `&#4;` case because it uses `lxml` in recovery mode. In .NET we must explicitly defuse the char-refs before handing the string to `XElement.Parse`.

### 7.6 Sentinel values (treat as missing)

Tally reports "no value set" for some master fields with sentinel strings rather than omitting the element:

| Field | Sentinel | Treat as |
|---|---|---|
| `<GSTHSNCODE>` | `"\x04 Not Found"` → after strip: `"Not Found"` | `null` HSN; fall through to `<HSNCODE>` if present |

Implemented in `TallyXmlMasterSource.SanitizeHsn`. Add new sentinels here as they are discovered from live data.

### 7.7 Live counts observed (April 20, 2026)

Against `http://192.168.1.13:9000`, company `dummy`:

- companies: 2 (`DEEN DAYAL ANAND KUMAR SARRAF`, `dummy`)
- ledgers: 397
- stock items: 11 (all HSNs returned as the `\x04 Not Found` sentinel)
- voucher types: 33 (includes legacy `Counter Sale` → parent `Sales`)

Saved raw response fixtures live at `/tmp/tally-*-response.xml` in the verification environment. The stock items response is exercised end-to-end by `TallyXmlLiveFixtureTests` when that file is present; the test no-ops when absent so CI stays green without live infrastructure.

---

## 8. Voucher write path

Section 7 documents the **read** path (masters). This section documents the **write** path: how the API posts a bill to Tally as a sales voucher.

### 8.1 Pipeline

Synchronous, in-process, driven by an operator click on Push / Retry / Repost:

1. `BillService.PushInternalAsync` transitions the bill to `posting` and saves (so a crash is recoverable).
2. Builds a `TallyPostRequest` carrying the bill header + `BillPayloadDto` from the current revision's snapshot. `Operation=Create` is the default; `Operation=Alter` is used only for `EditedAfterPush=true` bills after resolving the old Tally `MASTER ID` from pre-edit audit.
3. Calls `ITallyPoster.PostAsync` which:
   - reads ledger mappings + active company from cloud settings via `ICloudSettingsService`
   - builds an Import Data envelope via `TallyXmlVoucherBuilder.Build`
   - sends it through `ITallyXmlClient.SendAsync` (one HTTP POST to Tally's localhost XML endpoint)
   - classifies the response
4. `BillService` records `tally.posted` or `tally.failed` audit, transitions the bill to `posted` or `failed`, saves, and returns the updated `BillResponse` to the desktop. Successful alter clears `EditedAfterPush` and stores the target `tallyMasterId`; failed alter keeps the old audit target for retry.

The desktop's Push button stays busy for the full duration of one Tally round-trip (typically 1–10 seconds).

### 8.2 Required cloud-settings ledger mappings

The voucher builder reads these from `ICloudSettingsService.GetEffectiveSettingsAsync` on every call (no cached config):

| Cloud-settings field | Purpose | Required |
|---|---|---|
| `Ledgers.SalesVoucherType` | `<VOUCHERTYPENAME>` + `VCHTYPE` attribute on `<VOUCHER>` | always (defaults to `Sales`) |
| `Ledgers.SalesLedger` | Sales-side `<LEDGERNAME>` inside each `ACCOUNTINGALLOCATIONS.LIST` | always |
| `Ledgers.CashLedger` | `<PARTYLEDGERNAME>` + offsetting Dr ledger when `bill.Payment` normalizes to `Cash` | always |
| `Ledgers.CreditDebitLedger` | `<PARTYLEDGERNAME>` + offsetting Dr ledger when `bill.Payment` normalizes to `Credit and debit` | always |
| `Ledgers.CgstLedger` + `Ledgers.SgstLedger` | Credit ledgers for CGST / SGST halves of `BillTotals.TaxTotal` | only when `TaxTotal != 0` |
| `Ledgers.DiscountLedger` | Debit ledger for `BillTotals.DiscountTotal` | only when `DiscountTotal != 0` |
| `Ledgers.RoundOffLedger` | Credit/debit ledger for `BillTotals.RoundOff` | only when `RoundOff != 0` |
| `Connection.ActiveCompanyName` | `<SVCURRENTCOMPANY>` scope for the import | always |

Missing config raises `VoucherBuildException` with `Terminal = true`, surfacing in the UI as a `failed` bill with `CONFIG_MISSING_*` error code — operator fixes the mapping in Settings, then clicks Retry.

The `{PartyLedger}` placeholder in `<PARTYLEDGERNAME>` and the offsetting Dr ledger entry is **resolved from the bill's payment mode**, not from the customer name. `bill.Payment` is normalized via [`PaymentMode.Normalize`](../src/ShowroomBilling.Contracts/Bills/PaymentMode.cs) into `Cash` or `Credit and debit`, then mapped to `Ledgers.CashLedger` or `Ledgers.CreditDebitLedger`. The free-text `bill.PartyName` (operator's customer label) is joined with `bill.Notes` and emitted as `<NARRATION>` — it never reaches `<PARTYLEDGERNAME>`. See [`05_tally_integration_contract.md` §3.1](05_tally_integration_contract.md) for the full derivation rule.

### 8.3 Envelope shape (`voucher-import-v1`)

This is the *Accounting Invoice* layout, live-verified against the `dummy` company. It matches the canonical envelope in §5.

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
            <ISINVOICE>Yes</ISINVOICE>
            <NARRATION>{PartyName} | {Notes}</NARRATION>

            <!-- 1. Party (Dr): AMOUNT is NEGATIVE; ISDEEMEDPOSITIVE=Yes for Dr-natured ledgers -->
            <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>{PartyLedger}</LEDGERNAME>
              <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
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

#### 8.3.1 Gotchas that produce silent `TALLY_NO_EFFECT`

These are all bugs that caused real failed pushes during V2 bring-up — each one produces the opaque response `<RESPONSE>Unknown Request, cannot be processed</RESPONSE>` (with `CREATED=0, ALTERED=0, ERRORS=0, EXCEPTIONS=0`), which `TallyPoster` classifies as `TALLY_NO_EFFECT`. None of them produce a useful LINEERROR or ERRORS count, so you *must* inspect the `requestExcerpt` and `responseExcerpt` on the `tally.failed` audit event.

1. **`<VERSION>1</VERSION>` in the Import Data header.** Tally Prime's Import Data handler rejects the request outright when `VERSION` is present. The Export request shape *requires* `<VERSION>`, which tempts copy-paste; Import requests *must* omit it. This was the single biggest blocker during V2 bring-up.
2. **Flat `ALLINVENTORYENTRIES.LIST` + `ACCOUNTINGALLOCATIONS.LIST` layout.** This is the Item-Invoice shape — Tally accepts it *on some companies* but returned `EXCEPTIONS=1` against `dummy`. The nested `INVENTORYALLOCATIONS.LIST` layout shown above is the canonical one.
3. **Sign convention inverted** (party positive, sales negative). Looks plausible if you think of `AMOUNT` as a signed Dr/Cr value, but produces `EXCEPTIONS=1`. Use the Dr-negative / Cr-positive convention above.
4. **Sales allocation uses gross `LineTotal` instead of net.** `lineTotal` in the bill payload is the gross (tax-inclusive) value. Posting it directly to Sales while *also* posting CGST + SGST separately double-counts tax, leaves the voucher imbalanced by exactly `TaxTotal`, and Tally rejects it silently.
5. **Ledger name mismatch between cloud-settings and Tally company.** `CONFIG_MISSING_CASH_LEDGER` / `CONFIG_MISSING_CREDIT_DEBIT_LEDGER` / `MISSING_PAYMENT_MODE` are caught pre-send, but every *other* ledger (CGST/SGST/RoundOff/Sales/Discount) is only validated by Tally itself — and a missing ledger here surfaces as the same `Unknown Request` response, never as a LINEERROR naming the offender. Use `List of Ledgers` via a Tally collection export to verify each name exists in the target company before debugging the XML.
6. **`ActiveCompanyName` doesn't match a company open in Tally.** Same symptom — request accepted, 0 created, 0 altered, no error. Verify with the `$$CurrentCompany` function export before blaming the voucher XML.

### 8.4 `REMOTEID` idempotency

`REMOTEID` on create imports is `post:{billId:N}:{revisionId:N}`. Tally enforces uniqueness on this field per company, so if a push happens twice against the same bill revision (e.g. network glitch, retry), Tally rejects the second import with `ERRORS > 0` — classified as `TALLY_ERRORS`, landing the bill in `failed`. The operator then decides: click Retry (same REMOTEID; Tally will reject again until the first post is manually voided or superseded), or Mark as Pushed (local-only attestation), or Revise (creates a new bill with a fresh REMOTEID).

Edited-after-push alter requests do not send `REMOTEID`; they target the old voucher with `TAGNAME="MASTER ID"` + `TAGVALUE="{oldTallyMasterId}"`.

### 8.5 Response classification

`TallyPoster.ClassifyResponse`, in order:

| Condition | Outcome | Error code |
|---|---|---|
| any `<LINEERROR>` descendant | `Failed` | `TALLY_LINEERROR` |
| `<ERRORS>` > 0 or `<EXCEPTIONS>` > 0 | `Failed` | `TALLY_ERRORS` |
| alter request and `CREATED > 0` | `Failed` | `TALLY_UNEXPECTED_CREATE_ON_ALTER` |
| create request and `CREATED + ALTERED <= 0` | `Failed` | `TALLY_NO_EFFECT` |
| alter request and `ALTERED <= 0` | `Failed` | `TALLY_NO_EFFECT` |
| otherwise | `Posted` | — |

`RemoteId` on create success: `LASTVCHID ?? LASTMID ?? request.IdempotencyKey`. `tallyMasterId` is captured from positive numeric `LASTVCHID`.

`RemoteId` on alter success: `LASTVCHID ?? LASTMID ?? request.TargetTagValue`; `tallyMasterId` is `LASTVCHID` when positive numeric, otherwise the alter target.

Transport failures (`HttpRequestException`, `TaskCanceledException` outside the caller token, `InvalidOperationException` from missing config) are classified as `Failed` with error codes `TALLY_HTTP` / `TALLY_TIMEOUT` / `TALLY_NOT_CONFIGURED`. The `BillPostingStatusResponse` returns the last audit event's `errorCode` + `errorMessage` so the operator sees what happened without digging through logs.

Success and failure audit events both carry truncated `RequestExcerpt` and `ResponseExcerpt` (≤4000 chars each) for diagnosis.

### 8.6 Terminal vs transient categorisation

Since V2 has no auto-retry, "terminal" vs "transient" is informational only — the operator decides what to do next. Typical categorisation:

- **Config errors → fix Settings, then Retry:** `CONFIG_MISSING_SALES_LEDGER`, `CONFIG_MISSING_CGST_LEDGER`, `CONFIG_MISSING_SGST_LEDGER`, `CONFIG_MISSING_ROUNDOFF_LEDGER`, `CONFIG_MISSING_DISCOUNT_LEDGER`, `CONFIG_MISSING_COMPANY`, `TALLY_NOT_CONFIGURED`.
- **Bill-content errors → Revise, then Push:** `MISSING_PAYMENT_MODE`, `NO_LINES`.
- **Transient Tally/network errors → just Retry:** `TALLY_HTTP`, `TALLY_TIMEOUT`, `TALLY_LINEERROR`, `TALLY_ERRORS`, `TALLY_NO_EFFECT`.

Every click is a fresh attempt — there is no automatic backoff or next-attempt clock.

---

## 9. Verification command

Current focused regression command:

```bash
python3 -m pytest -q tests/test_client.py tests/test_xml_builder.py tests/test_tally_contract.py
```

Expected result after the April 11, 2026 fix:

- `30 passed`
