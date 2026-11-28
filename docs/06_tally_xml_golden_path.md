# Tally XML Golden Path

**Status:** Canonical reference for the live Tally XML read and write paths  
**Last live verification:**
- write path (sales voucher, Tally Wrapper C# builder): April 23, 2026
- read path (master collections): April 20, 2026

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
| Ledgers | `Ledger` | `Name` |
| Stock items | `Stock Item` | `Name, BaseUnits, GSTHSNCode` |
| Voucher types | `Voucher Type` | `Name` |

Tally may omit requested fields. A master entry requires a name. Other fields are optional.

### 3.4 Response shape

Read master entries from `BODY/DATA/COLLECTION` or a bare `COLLECTION`, matching element names by their local part. A zero header status, a data-level `LINEERROR`, a missing collection, unexpected entries or invalid rows fail the refresh. The previous snapshot and its timestamp survive a failed read. A successful empty collection replaces the snapshot with an empty list.

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
        <LEDGER NAME="Sample Customer" RESERVEDNAME="">
          <PARENT TYPE="String">Sundry Debtors</PARENT>
          <PARTYGSTIN TYPE="String">EXAMPLE-GSTIN</PARTYGSTIN>
          <ISREVENUE TYPE="Logical">No</ISREVENUE>
          <LANGUAGENAME.LIST>
            <NAME.LIST TYPE="String"><NAME>Sample Customer</NAME></NAME.LIST>
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
- **Numeric character references** like `&#4;` in text content. For example, a `<STOCKITEM>` can contain `<GSTHSNCODE>&#4; Not Found</GSTHSNCODE>`. `&#4;` resolves to `U+0004` (`EOT`), which is not a valid XML 1.0 character. `.NET`'s `XmlReader` rejects the reference during entity decoding with `XmlException: hexadecimal value 0x04, is an invalid character`, even though the reference itself is syntactically legal.

Mitigation (`TallyXmlClient.StripInvalidXmlControlChars`):

- before parsing, regex-strip raw `[\x00-\x08\x0B\x0C\x0E-\x1F]` bytes from the response body. Tab (`\x09`), LF (`\x0A`), CR (`\x0D`) are preserved.
- then regex-match every `&#<decimal>;` / `&#x<hex>;` in the body; drop the reference when the code point is invalid for XML 1.0 (C0 except tab/LF/CR, and C1 except NEL `U+0085`). Valid references (e.g. `&#65;` for `A`) pass through unchanged.

**Tally also writes prefixes it never declares.** User-defined fields come as `<UDF:NAME.LIST DESC="…" ISLIST="YES" TYPE="String" INDEX="…">` with no `xmlns:UDF` anywhere in the answer, which `XElement.Parse` refuses (`'UDF' is an undeclared prefix`). The master lists and the single-voucher lookups never carried one, but the first live read of a whole year's sales vouchers did (26 November 2026), and Settings → Tally check answered "Nothing was compared". `TallyXmlClient.ParseResponse` therefore reads the answer with `UDF` declared as `TallyUDF` (the namespace a voucher import names) and gives any other undeclared prefix on an element or attribute a namespace of its own. Every reader matches names by their local part, so the namespaces change nothing else. With it the same live read returned the whole year's vouchers in under a second. `TallyXmlResponseParseTests` holds it.

### 3.6 Sentinel values (treat as missing)

Tally reports "no value set" for some master fields with sentinel strings rather than omitting the element:

| Field | Sentinel | Treat as |
|---|---|---|
| `<GSTHSNCODE>` | `"\x04 Not Found"` → after strip: `"Not Found"` | `null` HSN; fall through to `<HSNCODE>` if present |

Implemented in `TallyXmlMasterSource.SanitizeHsn`. Add new sentinels here as they are discovered from live data.

## 4. Voucher write path

Section 3 documents the **read** path (masters). This section documents the **write** path: how the API posts a bill to Tally as a sales voucher.

### 4.1 Pipeline

Synchronous, in-process, driven by an operator click on Push / Retry / Repost:

1. `BillPostingWorkflow.PushInternalAsync` transitions the bill to `posting` and saves (so a crash is recoverable).
2. Builds a `TallyPostRequest` carrying the bill header + `BillPayloadDto` from the current revision's snapshot. `Operation=Create` is the default; `Operation=Alter` is used only for `EditedAfterPush=true` bills, after the old voucher's `MASTER ID` and the date it has in Tally have been read from Tally (§4.3; this read happens before step 1's claim).
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
| `Ledgers.BankChargesLedger` | Credit ledger for `BillTotals.BankCharges` (charged on top of the bill, after GST, with no GST of its own; the party leg's debit includes it). With none chosen the push is refused before anything is sent (`CONFIG_MISSING_BANK_CHARGES_LEDGER`) | only when `BankCharges != 0` |
| `Ledgers.RoundOffLedger` | Credit/debit ledger for `BillTotals.RoundOff` | only when `RoundOff != 0` |
| `Connection.ActiveCompanyName` | `<SVCURRENTCOMPANY>` scope for the import | always |
| `Print.CompanyState` | `<STATENAME>`, `<PLACEOFSUPPLY>`, `<CONSIGNEESTATENAME>` GST transaction state fields | when configured |
| `Print.CompanyCountry` | `<COUNTRYNAME>` for the voucher view plus `<COUNTRYOFRESIDENCE>` / `<CONSIGNEECOUNTRYNAME>` for GST/GSTR-1 fields | when configured |

A missing mapping raises `VoucherBuildException` before anything is sent, surfacing in the UI as a `failed` bill with a `CONFIG_MISSING_*` error code — the operator fixes the mapping in Settings, then clicks Retry. The builder also checks that the ledger amounts net to zero exactly as they will be sent, and fails with `VOUCHER_UNBALANCED` if they do not.

The `{PartyLedger}` placeholder in `<PARTYLEDGERNAME>` and the offsetting Dr ledger entry is **resolved from the bill's payment mode**, not from the customer name. `bill.Payment` is normalized via [`PaymentMode.Normalize`](../src/ShowroomBilling.Contracts/Bills/PaymentMode.cs) into `Cash` or `Credit and debit`, then mapped to `Ledgers.CashLedger` or `Ledgers.CreditDebitLedger`. The free-text `bill.PartyName` (operator's customer label) never reaches `<PARTYLEDGERNAME>`. It is sent as `<PARTYMAILINGNAME>`, the buyer's mailing name in the voucher's party details (the party ledger when the bill names no customer), and joined with `bill.Notes` as `<NARRATION>`. See [`05_tally_integration_contract.md` §3.1](05_tally_integration_contract.md) for the full derivation rule.

`<PARTYNAME>` deliberately uses the same resolved party ledger. It is a Tally GST transaction field, not the free-text customer label.

`<PARTYMAILINGNAME>` was added on 20 November 2026 and has not yet been pushed to a live company. Tally's sample XML page (§6.1) does not show it, so it is unconfirmed: push one bill with a customer name to the test company and open the voucher's party details before relying on it.

### 4.3 Envelope shape

This is the *Accounting Invoice* layout that `TallyXmlVoucherBuilder.Build` produces, live-verified against a test company. Amounts are written with two decimals. `VOUCHERNUMBER`, the three state elements, the three country elements and `NARRATION` are left out when they have no value.

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
          <VOUCHER REMOTEID="{IdempotencyKey}" VCHTYPE="{SalesVoucherType}" ACTION="Create" OBJVIEW="Accounting Voucher View">
            <PERSISTEDVIEW>Accounting Voucher View</PERSISTEDVIEW>
            <DATE>{yyyyMMdd}</DATE>
            <EFFECTIVEDATE>{yyyyMMdd}</EFFECTIVEDATE>
            <VOUCHERTYPENAME>{SalesVoucherType}</VOUCHERTYPENAME>
            <VOUCHERNUMBER>{InvoiceNumber}</VOUCHERNUMBER>
            <PARTYLEDGERNAME>{PartyLedger}</PARTYLEDGERNAME>
            <PARTYNAME>{PartyLedger}</PARTYNAME>
            <PARTYMAILINGNAME>{PartyName, or PartyLedger when blank}</PARTYMAILINGNAME>
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
            <!-- 5. Bank charges (Cr): AMOUNT is POSITIVE, ISDEEMEDPOSITIVE No (only if BankCharges > 0) -->
            <!-- 6. Round-off: AMOUNT preserves sign (positive = Cr income; negative = Dr expense) -->
          </VOUCHER>
        </TALLYMESSAGE>
      </REQUESTDATA>
    </IMPORTDATA>
  </BODY>
</ENVELOPE>
```

For an edited-after-push bill, the same voucher body is emitted with alter attributes instead of a create `REMOTEID`:

```xml
<VOUCHER DATE="{voucherDateInTally:yyyyMMdd}" VCHTYPE="{SalesVoucherType}" ACTION="Alter"
         TAGNAME="MASTER ID" TAGVALUE="{oldTallyMasterId}" OBJVIEW="Accounting Voucher View">
  <PERSISTEDVIEW>Accounting Voucher View</PERSISTEDVIEW>
  <DATE>{billDate:yyyyMMdd}</DATE>
  <EFFECTIVEDATE>{billDate:yyyyMMdd}</EFFECTIVEDATE>
  <VOUCHERTYPENAME>{SalesVoucherType}</VOUCHERTYPENAME>
  <VOUCHERNUMBER>{InvoiceNumber}</VOUCHERNUMBER>
  ...
</VOUCHER>
```

Tally finds the voucher to alter by `TAGVALUE` **together with the `DATE` attribute**, which must be the date the voucher has in Tally now; the bill's (possibly edited) date goes in the `<DATE>` element, and Tally moves the voucher to it. An alter Tally cannot match is imported as a new voucher, answered `CREATED=1 ALTERED=0` with no error. Found the hard way:

- **Live, 25 November 2026:** the first alter ever sent to a live company had no `DATE` attribute. Tally left the old voucher untouched and created a second one; with the voucher type's numbering set to Automatic / Auto Renumber, the new voucher took a number in the middle of the series and every later voucher's number moved up by one. `TALLY_UNEXPECTED_CREATE_ON_ALTER` caught it (§4.5), but only after the fact.
- **TallyPrime 7.2, a copy of a test company, the same day** (the voucher type set to Automatic / Auto Renumber like the live one; every request read back afterwards by master id, alter id, date, number, ledger and inventory lines):

| Alter sent | Tally's answer | What Tally then held |
|---|---|---|
| As above but no `DATE` attribute (the old request) | `CREATED=1` | A second voucher; the next voucher renumbered |
| `DATE` attribute = the voucher's date (`yyyyMMdd` or `1-Jun-2026`, either header form, `TAGNAME="MASTER ID"` or `"Voucher Number"`) | `ALTERED=1` | Same voucher, same number. In the accounting view every ledger and inventory line is **replaced** (a removed item line goes, quantities and amounts change); in the invoice view only the narration changes |
| The same alter sent twice | `ALTERED=1` twice | No line doubled |
| `DATE` attribute = the old date, `<DATE>` = a later date | `ALTERED=1` | Voucher moved to the new date; Auto Renumber renumbers by date |
| `DATE` attribute = the new date | `CREATED=1` | A second voucher |
| An invoice-view voucher, alter with `OBJVIEW` + `<PERSISTEDVIEW>` as above | `ALTERED=1` | Lines replaced, voucher now in the accounting view |
| `REMOTEID` = the voucher's GUID, or a `<GUID>` child, no `TAGNAME` | `CREATED=1` | A second voucher |

Every voucher is sent in the accounting view, creates too: `OBJVIEW="Accounting Voucher View"` on `<VOUCHER>` and `<PERSISTEDVIEW>` as its first child. Without the markers a live company stored the app's creates in either view, about half in each, and an alter replaces the lines only of a voucher in the accounting view (an alter carrying the markers converts an invoice-view one, as the table shows). On TallyPrime 7.2 with a copy of a test company (25 November 2026) a create with the markers was stored in the accounting view with the same ledger and inventory lines as one without.

The date comes from Tally, never from the bill: before every alter (and before the bill is claimed) the API reads the voucher. The id is the one the last successful pre-edit `tally.posted` audit records, preferring `details.tallyMasterId` and falling back to numeric legacy `details.remoteId`, and Tally is asked for the voucher with that id:

```xml
<COLLECTION NAME="TW_VoucherById">
  <TYPE>Voucher</TYPE>
  <FETCH>MasterID, VoucherNumber, VoucherTypeName, Date, Amount</FETCH>
  <FILTER>TW_VoucherIdIs</FILTER>
</COLLECTION>
<SYSTEM TYPE="Formulae" NAME="TW_VoucherIdIs">$MasterID = {id}</SYSTEM>
```

in the same envelope as the lookup by number below. The filter is the id alone (a master id names one voucher within the company) and the type is checked on this side, so a voucher type whose name a TDL formula cannot carry can still be read. No such voucher (deleted in Tally, or dated outside the bill's fiscal year) is `TALLY_VOUCHER_NOT_FOUND` with nothing sent: sending the alter anyway would create a second voucher. The id is also bound to its company: `tally.posted` records the company each accepted voucher went to, and an alter is refused with `TALLY_COMPANY_CHANGED` when Settings now name another one, checked before the read; the company and voucher type of the read are carried into the alter request, and `TallyPoster` will not send it anywhere else. An id from a push that recorded no company (one made before 25 November 2026) is accepted only when the voucher shows the date and grand total the bill had when Tally was last said to hold it. If that audit is missing (a bill an admin marked as pushed), the API asks Tally for the voucher by number instead:

```xml
<ENVELOPE>
  <HEADER>
    <VERSION>1</VERSION>
    <TALLYREQUEST>Export</TALLYREQUEST>
    <TYPE>Collection</TYPE>
    <ID>TW_VouchersByNumber</ID>
  </HEADER>
  <BODY>
    <DESC>
      <STATICVARIABLES>
        <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
        <SVFROMDATE>{fiscalYearStart:yyyyMMdd}</SVFROMDATE>
        <SVTODATE>{fiscalYearEnd:yyyyMMdd}</SVTODATE>
        <SVCURRENTCOMPANY>{ActiveCompanyName}</SVCURRENTCOMPANY>
      </STATICVARIABLES>
      <TDL>
        <TDLMESSAGE>
          <COLLECTION NAME="TW_VouchersByNumber">
            <TYPE>Voucher</TYPE>
            <FETCH>MasterID, VoucherNumber, VoucherTypeName, Date, Amount</FETCH>
            <FILTER>TW_VoucherNumberIs</FILTER>
          </COLLECTION>
          <SYSTEM TYPE="Formulae" NAME="TW_VoucherNumberIs">$VoucherNumber = "{number}" AND $VoucherTypeName = "{SalesVoucherType}"</SYSTEM>
        </TDLMESSAGE>
      </TDL>
    </DESC>
  </BODY>
</ENVELOPE>
```

It reads each `<VOUCHER>` in the answer and keeps those whose `<VOUCHERNUMBER>` (or, for the read by id, `<MASTERID>`) is the one asked for, whose type (`<VOUCHERTYPENAME>` or the `VCHTYPE` attribute) is the sales voucher type and whose `<DATE>` is inside the fiscal year asked for (Tally does not always keep to `SVFROMDATE` / `SVTODATE`), taking `<MASTERID>`, `<DATE>` and the voucher-level `<AMOUNT>` (the party amount, negative). A voucher that matches but has no usable id, date or type is an error, not a miss (the read by id does not filter on the type, so it must be there to be checked), and so is one id listed with two dates. An answer that is not a collection (a bare `<RESPONSE>Unknown Request…</RESPONSE>`, or `<STATUS>0</STATUS>`; Tally sends both with HTTP 200) is an error too, never "no such voucher": the push is refused like a failed preflight. Found by number, exactly one is the alter target only when its date and total equal the date and grand total of the revision current when the bill was marked as pushed, because under Auto Renumber the number may have moved to another sale; otherwise `TALLY_VOUCHER_MISMATCH`. None is `TALLY_VOUCHER_NOT_FOUND`, several is `TALLY_VOUCHER_AMBIGUOUS`, and in every one of these cases nothing is sent and no replacement voucher is created. A voucher whose `<ISDELETED>` is `Yes` (one Tally keeps only as deleted) does not count.

Run read-only against TallyPrime on 14 November 2026, through the app's own lookup code: for a posted bill it found exactly one voucher, and its `<MASTERID>` was the `tallyMasterId` recorded in that bill's `tally.posted` audit; for a number that does not exist it found none. Each answer took about 0.2 s. Two things about the answer, both pinned in `TallyVoucherLookupTests`:

- It starts with a `<BODY><DESC><CMPINFO>` block of counts, one of which is an element named `<VOUCHER>` holding a number. It is there even when no voucher matched (the `<COLLECTION>` is then empty). The check on this side passes over it, because it has no `<VOUCHERNUMBER>`.
- A voucher comes as `<VOUCHER REMOTEID=… VCHKEY=… VCHTYPE=… OBJVIEW=…>` with its native fields (`DATE`, `GUID`, `VOUCHERTYPENAME`, `VOUCHERNUMBER`, `ISDELETED`, `ISINVOICE`, …) beside `<MASTERID>`.

The read by id (`$MasterID` filter) was run read-only against a live TallyPrime 7.2 on 25 November 2026 and returned the voucher with its `<DATE>`. The alter that follows either read has been run against TallyPrime 7.2 with a copy of a test company (the table above), and against the stand-in Tally, which since then matches an alter the way Tally does and creates a voucher when it cannot.

**Sign convention (Accounting Invoice view).** Tally's voucher import interprets the `AMOUNT` sign as the net change from the company's Cr-side perspective, not directly as Dr/Cr:

- **Dr legs** (Cash/party receiving, Discount expense) → **negative** `AMOUNT`
- **Cr legs** (Sales revenue, CGST, SGST, bank charges, positive Round-off income) → **positive** `AMOUNT`
- **Round-off** preserves sign: `+0.01` posts Cr 0.01 (income); `-0.01` posts Dr 0.01 (expense)
- **Inventory `AMOUNT` matches its parent sales ledger entry** (positive for a Cr sale). Per-line amounts are the *net* share of Subtotal, not the gross `LineTotal` — otherwise the voucher double-counts tax and fails to balance.

The builder uses proportional allocation so multi-line bills with rounding drift still sum exactly to `bill.Totals.Subtotal`: each line gets `Math.Round(LineTotal / ΣLineTotal × Subtotal, 2)`, with any residual absorbed by the last line.

#### 4.3.1 Gotchas that produce silent `TALLY_NO_EFFECT`

These are all bugs that caused real failed pushes during Tally Wrapper bring-up — each one produces the opaque response `<RESPONSE>Unknown Request, cannot be processed</RESPONSE>` (with `CREATED=0, ALTERED=0, ERRORS=0, EXCEPTIONS=0`), which `TallyPoster` classifies as `TALLY_NO_EFFECT`. None of them produce a useful LINEERROR or ERRORS count, so you *must* inspect the `requestExcerpt` and `responseExcerpt` on the `tally.failed` audit event.

1. **`<VERSION>1</VERSION>` in the Import Data header.** Tally Prime's Import Data handler rejects the request outright when `VERSION` is present. The Export request shape *requires* `<VERSION>`, which tempts copy-paste; Import requests *must* omit it. This was the single biggest blocker during Tally Wrapper bring-up.
2. **Flat `ALLINVENTORYENTRIES.LIST` + `ACCOUNTINGALLOCATIONS.LIST` layout.** This is the Item-Invoice shape — Tally accepts it *on some companies* but returned `EXCEPTIONS=1` in the verification company. The nested `INVENTORYALLOCATIONS.LIST` layout shown above is the canonical one.
3. **Sign convention inverted** (party positive, sales negative). Looks plausible if you think of `AMOUNT` as a signed Dr/Cr value, but produces `EXCEPTIONS=1`. Use the Dr-negative / Cr-positive convention above.
4. **Sales allocation uses gross `LineTotal` instead of net.** `lineTotal` in the bill payload is the gross (tax-inclusive) value. Posting it directly to Sales while *also* posting CGST + SGST separately double-counts tax, leaves the voucher imbalanced by exactly `TaxTotal`, and Tally rejects it silently.
5. **Ledger name mismatch between cloud-settings and Tally company.** `CONFIG_MISSING_CASH_LEDGER` / `CONFIG_MISSING_CREDIT_DEBIT_LEDGER` / `MISSING_PAYMENT_MODE` are caught pre-send, but every *other* ledger (CGST/SGST/RoundOff/Sales/Discount) is only validated by Tally itself — and a missing ledger here surfaces as the same `Unknown Request` response, never as a LINEERROR naming the offender. Use `List of Ledgers` via a Tally collection export to verify each name exists in the target company before debugging the XML.
6. **`ActiveCompanyName` doesn't match a company open in Tally.** Same symptom — request accepted, 0 created, 0 altered, no error. Verify with the `$$CurrentCompany` function export before blaming the voucher XML.

### 4.4 `REMOTEID` idempotency

`REMOTEID` on create imports is `post:{billId:N}:{revisionId:N}`; an explicit repost sends the caller's own key instead.

**A create sent again with a `REMOTEID` that a voucher of the company already carries makes no second voucher and is not refused: Tally alters that voucher** (`CREATED=0 ALTERED=1`, the same master id in `LASTVCHID`), even when the content differs. Found on TallyPrime 7.2 with a copy of a test company on 25 November 2026. Until then this section said Tally refused the repeat with `ERRORS > 0`; that had never been tried. `TallyPoster` takes the answer as accepted and the `tally.posted` audit records it as `updatedExistingVoucher: true`, so it can be told apart from a voucher made new. So a push repeated on one revision (a Retry, or a push whose answer was lost) overwrites the voucher it made the first time, and a Repost with a key Tally has seen overwrites that voucher. An edit saves a new revision, and with it a fresh `REMOTEID`; a bill Tally holds is then pushed as an alter (§4.3), never as a create.

Live finding (April 11, 2026): after an import that failed with `EXCEPTIONS=1`, a second, otherwise valid import that reused the same `REMOTEID` failed too, and one with a fresh `REMOTEID` succeeded. Tally's documentation offers a reason that has not been checked here: the first voucher may have been kept as an exception (§6.3).

Edited-after-push alter requests do not send `REMOTEID`; they target the old voucher with `TAGNAME="MASTER ID"` + `TAGVALUE="{oldTallyMasterId}"` + `DATE="{the date it has in Tally}"` (§4.3). An export shows Tally's own GUID in a voucher's `REMOTEID`, never `post:…`, and a re-import naming that GUID creates another voucher rather than altering it; yet Tally still matches a later create on the `REMOTEID` the first one sent (above).

### 4.4.1 How Tally numbers the voucher, and reading it back

Tally may file a voucher under another number than the bill's. What the voucher type's numbering method does with the `<VOUCHERNUMBER>` sent (TallyPrime 7.2: a copy of a test company on 25 November 2026, and a live company on 26 November 2026 for Auto Retain):

| Numbering method | The number sent | Other vouchers |
|---|---|---|
| Automatic, Auto Renumber (renumber on insertion and deletion) | Ignored | Numbered by date, and within a day in the order the vouchers were created. A voucher dated earlier than others, or one deleted, renumbers every later voucher |
| Automatic, Auto Retain (retain the original voucher number) | Ignored | Each voucher keeps the number it was given when created; numbers are handed out in creation order, whatever the dates |
| Automatic (Manual Override), Manual | Kept | Never renumbered. A repeated number is accepted without a word; with Prevent Duplicates on, it is kept as an exception (`EXCEPTIONS=1`) |

Under Auto Renumber the order within a day is the order of creation, so two bills of one day pushed out of number order get each other's numbers in Tally (bill 2 pushed before bill 1 is filed as 1). Tally check shows these as same-day swaps. Pushing a day's bills in number order avoids them: **Push all pending** goes by bill date and then number, **Push selected** and **Retry selected** send the ticked bills in that order too (the server gives it, from the bills as they are now; a retry of several stops at the first bill Tally does not take), and since 26 November 2026 a push or retry that would go past an earlier-numbered bill of the same or an earlier day that is not in Tally yet asks first ("Push before …?", **Push anyway** or Cancel; `POST /api/bills/push-order`, [09](09_api_spec.md)). An earlier day counts too: when that bill does reach Tally, Auto Renumber moves every later voucher up by one, and the numbers already read back for them are no longer what Tally shows. With the zero fill on, Tally also writes its own numbers to a fixed width ("…/051" for "…/51").

**Do not change the numbering method of a company that has vouchers.** Under Auto Renumber Tally keeps no number on the voucher; it works the numbers out. Changing the method wipes them, and changing it back deals them out again:

- By XML on a copy of a test company (25 November 2026), Auto Renumber → Manual Override blanked the numbers of the vouchers made under Auto Renumber, and changing it back renumbered every voucher.
- Through Tally's own screen on a live company (26 November 2026), the same change blanked every sales voucher number of the year. Changing the method back to Automatic came back as **Auto Retain**, which dealt the numbers out in creation order: a bill dated in October whose voucher was created in September took a September number, and every bill after it moved up by one. Setting the sub-method back to **Auto Renumber** dealt them out by date again and gave back exactly the numbers the vouchers had before. No voucher, date or amount was lost at any step.

So the voucher type stays on Automatic / Auto Renumber. The app does not depend on Tally keeping its number: it reads every voucher back (below) and Settings → Tally check lists any number Tally files differently.

The setting can be read without changing anything, with a collection export of the voucher type (the envelope of the lookup by number in §4.3, no period needed):

```xml
<COLLECTION NAME="TW_VchTypeNumbering">
  <TYPE>VoucherType</TYPE>
  <FETCH>Name, Parent, NumberingMethod, PreventDuplicates, PrefillZero, VoucherNumberSeries</FETCH>
  <FILTER>TW_TypeIs</FILTER>
</COLLECTION>
<SYSTEM TYPE="Formulae" NAME="TW_TypeIs">$Name = "{SalesVoucherType}"</SYSTEM>
```

The answer holds a `VOUCHERNUMBERSERIES.LIST` named `Default` with `NUMBERINGMETHOD` (`Automatic`, …), `NUMBERINGSUBMETHOD` (`Auto Renumber` or `Auto Retain`), `WIDTHOFNUMBER`, `PREFILLZERO` and the prefix list. The voucher type's own `NUMBERINGMETHOD` can read `None` while the series says `Automatic`; the series is what counts.

After every push Tally accepts, the API reads the voucher back by its master id (the `TW_VoucherById` request of §4.3, the id from `LASTVCHID`, or the alter's target) and records what Tally filed it under in a `tally.voucher.filed` audit event: `tallyMasterId`, `voucherNumberInTally`, `voucherDateInTally`, `totalInTally`, `invoiceNumber`, `numberDiffers` (also true for a voucher Tally shows no number for) and `pushAuditId`, the id of the push's own `tally.posted` row. The push's answer then carries that number as `tallyVoucherNumber` and the Bills list shows it ([09](09_api_spec.md)), for as long as that push is the latest word on what Tally holds and the read-back names the voucher the push recorded (another counter may push or mark the bill while the read runs). The read happens after the outcome is saved and changes nothing: one that fails, finds nothing, or was made in another company than the push went to is logged at warning and the number is left unknown. It is bounded at 10 seconds; on the lab copy it took about 0.13 s.

### 4.4.2 The year's sales vouchers (Settings → Tally check)

`GET /api/tally-check/compare` asks Tally for every sales voucher of a fiscal year in one request, the envelope of the lookup by number in §4.3 with this collection:

```xml
<COLLECTION NAME="TW_SalesVouchers">
  <TYPE>Voucher</TYPE>
  <FETCH>MasterID, VoucherNumber, VoucherTypeName, Date, Amount</FETCH>
  <FILTER>TW_VoucherTypeIs</FILTER>
</COLLECTION>
<SYSTEM TYPE="Formulae" NAME="TW_VoucherTypeIs">$VoucherTypeName = "{SalesVoucherType}"</SYSTEM>
```

Each voucher in the answer goes through the same checks on this side as the lookups (its type, its date inside the fiscal year, not deleted, an id and a date it can be named by). A voucher type whose name holds a double quote is refused, since the formula cannot carry it. The server then sets the vouchers against the bills: a bill is matched by the master id its last push recorded, or by its number (allowing zeros in front of the last digits) when no push recorded one; see [05 §4](05_tally_integration_contract.md#settings--tally-check).

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

`RemoteId` on create success: `LASTVCHID ?? LASTMID ?? request.IdempotencyKey`. `tallyMasterId` is captured from positive numeric `LASTVCHID`, which Tally documents as the master id of the last imported voucher (§6.2).

`RemoteId` on alter success: `LASTVCHID ?? LASTMID ?? request.TargetTagValue`; `tallyMasterId` is `LASTVCHID` when positive numeric, otherwise the alter target.

Transport failures after a voucher write begins are classified as `Unknown`: `TALLY_TRANSPORT_UNKNOWN` for HTTP/transport errors, `TALLY_TIMEOUT` for the configured timeout, and `TALLY_RESPONSE_UNKNOWN` for an unusable response. They move the bill to `reconciliation_required`. The one transport error that is not ambiguous is a connection that was never made (refused, or the host name did not resolve): nothing was sent, so it is `Failed` with `TALLY_UNREACHABLE`. Missing configuration is known before sending and remains `Failed` with `TALLY_NOT_CONFIGURED`. Any other failure while loading settings or building/serializing the voucher XML happens before a byte is sent, so it is `Failed` with `TALLY_PREPARE_FAILED` (and `BillValidator` rejects XML-invalid control characters in text fields at save time). A response that changed something in Tally while also reporting an error is never a definite failure: Retry/Repost from `failed` would duplicate the voucher. `BillPostingStatusResponse` returns the latest problem's `errorCode` + `errorMessage`.

The `tally.failed` and `tally.outcome.unknown` audit events carry truncated `requestExcerpt` and `responseExcerpt` (≤4000 chars each) for diagnosis.

### 4.6 What the operator does next

There is no auto-retry: the operator decides what to do next. A `failed` bill cannot be revised; it is edited (which returns it to `pending`) or retried.

- **Config errors → fix Settings, then Retry:** `CONFIG_MISSING_SALES_LEDGER`, `CONFIG_MISSING_CASH_LEDGER`, `CONFIG_MISSING_CREDIT_DEBIT_LEDGER`, `CONFIG_MISSING_CGST_LEDGER`, `CONFIG_MISSING_SGST_LEDGER`, `CONFIG_MISSING_ROUNDOFF_LEDGER`, `CONFIG_MISSING_DISCOUNT_LEDGER`, `CONFIG_MISSING_COMPANY`, `TALLY_NOT_CONFIGURED`.
- **Tally was closed → open TallyPrime, then Retry:** `TALLY_UNREACHABLE`.
- **Bill-content errors → Edit, then Push:** `MISSING_PAYMENT_MODE`, `NO_LINES`, `VOUCHER_UNBALANCED`.
- **Ambiguous write outcomes → verify Tally, then resolve:** `TALLY_TRANSPORT_UNKNOWN`, `TALLY_TIMEOUT`, `TALLY_RESPONSE_UNKNOWN`, `TALLY_PARTIAL_EFFECT`, `TALLY_UNEXPECTED_CREATE_ON_ALTER`, `TALLY_UNEXPECTED`.
- **Definite Tally rejection → correct data/config, then Retry or Edit:** `TALLY_LINEERROR`, `TALLY_ERRORS`, `TALLY_NO_EFFECT`, `TALLY_PREPARE_FAILED`.
- **Edited bill whose voucher cannot be named → check Tally:** `TALLY_VOUCHER_NOT_FOUND` (the recorded voucher is gone from Tally, or no voucher has the bill's number: renumber it in Tally and Retry, or Mark as Pending if it is not there), `TALLY_VOUCHER_AMBIGUOUS` (several with that number: remove or renumber the extra one, then Retry), `TALLY_VOUCHER_MISMATCH` (the one voucher with that number shows another date or total: change it in Tally by hand and Mark as Pushed), `TALLY_COMPANY_CHANGED` (pushed to another company than Settings now names: choose that company again, or change the voucher by hand and Mark as Pushed), `TALLY_ALTER_TARGET_MISSING` (no recorded id and no lookup). Nothing is sent to Tally and no replacement voucher is created.

Every voucher click makes one HTTP attempt — there is no automatic voucher retry, backoff, or next-attempt clock. Safe XML reads retain the short transport retry pipeline.

---

## 5. Verification command

Current focused regression command:

```powershell
dotnet test --project tests/ShowroomBilling.Tests/ShowroomBilling.Tests.csproj --filter "FullyQualifiedName~TallyPosterTests|FullyQualifiedName~TallyXmlRetryPolicyTests|FullyQualifiedName~TallyXmlVoucherBuilderTests"
```

---

## 6. Tally's own documentation

Read on 18 November 2026. Use these pages to check a request shape, not as proof of behaviour: they describe file import from Tally's menu far more than XML sent over HTTP, and nothing in this section has been run against a live company unless it says so. The change control in §1 still holds.

### 6.1 Pages

All on `help.tallysolutions.com` unless marked.

| Page | What it holds |
|---|---|
| [Sample XML](https://help.tallysolutions.com/sample-xml/) | The most useful single page: sales voucher samples (as voucher, as invoice), voucher alteration, voucher cancellation, an object export (a ledger), and what the fields of the import answer mean |
| [Exceptions Report](https://help.tallysolutions.com/data-exceptions-resolutions-in-tallyprime/) | What Tally does with imported data that has a problem (§6.3) |
| [Import Data FAQ](https://help.tallysolutions.com/tally-prime/data-exchange-tally-prime/import-data-faq/) | The import settings under Alt+O → Configuration, and the `Tally.imp` log |
| [Import File definition and related events](https://help.tallysolutions.com/?p=180668) | The `Response Report` attribute and `$$LastImportError`: a TDL loaded in Tally can make the import answer carry the error text |
| [Integration initiated from third-party applications](https://help.tallysolutions.com/tallyprime-as-server/) | The shortest statement of the import answer, and that a duplicate request shows in the error count |
| [XML integration](https://help.tallysolutions.com/xml-interface/) and [Pre-requisites for integrations](https://help.tallysolutions.com/pre-requisites-for-integrations/) | The header fields of a request, and `STATUS` in an answer |
| [Case Study II](https://help.tallysolutions.com/article/DeveloperReference/integration-capabilities/case_study_2.htm) | Written for Tally.ERP 9: an alter that names its voucher by `MASTER ID` |
| [Import data from JSON or XML](https://help.tallysolutions.com/import-data-from-xml-or-json/) | Import of a JSON file, from Release 7.0 |

Not from Tally, so weigh them as one person's report: a [Stack Overflow answer on `REMOTEID`](https://stackoverflow.com/questions/46767573/what-is-the-significance-of-remoteid-attribute-in-tally-voucher-xml-file) and a vendor's [integration guide](https://perimattic.com/products/erpplugs/learn/tally-api-integration/).

Tally Connector, a tool inside TallyPrime Developer, sends a request and shows the answer. It is the quick way to try a shape before changing the builder.

### 6.2 What they settle

- **`LASTVCHID` is the master id of the last imported voucher**, and `LASTMID` always comes back as 0. So the id recorded in `tally.posted` is the right alter target, and the fallback to `LASTMID` in §4.5 never supplies a value. On an alter that matched, `LASTVCHID` is the altered voucher's id.
- **An alter names its voucher by master id, or by voucher number, each with type and date.** Every documented sample puts `DATE` and `VCHTYPE` on the `VOUCHER` tag. The `MASTER ID` form of §4.3 is a documented one, but the samples do not say that the `DATE` attribute is required or that an alter Tally cannot match becomes a new voucher; both were found live and confirmed on TallyPrime 7.2 (§4.3).
- **Cancelling a voucher is a documented request**: `ACTION="Cancel"` with the voucher number, type and date. This app does not send it; Void refuses a bill whose voucher Tally holds and leaves the cancelling to the operator.
- **Both header forms are documented.** The voucher samples on the Sample XML page use `VERSION` / `TALLYREQUEST` `Import` / `TYPE` `Data` / `ID` `Vouchers`; the form this app sends, `TALLYREQUEST` `Import Data` with no `VERSION`, is on the other pages. §4.3.1 item 1 is about adding `VERSION` to the second form.
- **A duplicate request shows in the error count**, which is what §4.4 saw.
- **An answer may carry `STATUS`**: 1 for success, 0 for failure. `TallyPoster` does not read it.
- **Tally's own logs** for a refused import: `Tally.imp` in the TallyPrime folder, and F1 → Troubleshooting → Event Log.

### 6.3 Open: a refused voucher may be kept as an exception

The Exceptions Report page says that imported data with a problem is, unless the import is set up otherwise, recorded as an exception while the import carries on. What is under exception stays out of the books and reports until somebody resolves it in Tally, and a voucher resolved there becomes a new voucher under the next free number. The reports are under Alt+O → Exceptions and Alt+Y → All Exceptions.

The page describes import from Tally's menu. No page says whether a voucher sent over HTTP is treated the same way, so this is not known for this app. It matters because `TallyPoster` settles an answer with `EXCEPTIONS > 0` and nothing created as `failed`, which is read everywhere as "Tally holds nothing":

- if Tally kept the voucher, resolving it in Tally later makes a second sale under another number, once the bill has been edited and pushed again;
- it would explain the live finding in §4.4, where a `REMOTEID` was refused after an import that had failed with `EXCEPTIONS=1`.

To settle it: push one voucher that names a ledger the test company does not have, then look in Tally under Alt+O → Exceptions. If the voucher is there, an answer with `EXCEPTIONS > 0` is not a definite failure and §4.5 has to change.

### 6.4 What no page answers

- **Whether Tally takes more than one request at a time.** Nothing official. The vendor guide says the listener handles requests one after another and that a large export holds up the rest.
- **Whether the import settings apply to XML over HTTP.** Alt+O → Configuration has a setting for overwriting a voucher that has the same GUID and one for ignoring errors during import. If they do apply, what a repeated `REMOTEID` does depends on how the Tally PC is set up. The Stack Overflow answer says a known `REMOTEID` updates the voucher; on TallyPrime 7.2 with its settings as installed it does (§4.4).
- **How to read one voucher back by master id or GUID.** The only object export shown is a ledger. The lookup of §4.3 is a filtered collection for that reason.
