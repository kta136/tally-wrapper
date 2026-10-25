# Bill hand-over

Another program on a counter PC (the sender) can hand Tally Wrapper's Desktop one or more sale bills. The sender's server builds and signs the hand-over, its web page opens it as a `tallywrapper:` link, and Windows starts the Desktop (or reaches the copy already running) with the link as its only argument. The Desktop checks the link, saves each bill as an **ordinary sale bill**, exactly as if it had been typed on the Invoice tab, prints it in its own format and tells the operator how it went.

Nothing marks a handed-over bill. It is created through the same `POST /api/bills/drafts` the Invoice tab uses, with the hand-over's save ID as its `ClientOperationId`, and changed through the same `PUT /api/bills/drafts/{id}`. There is no column, flag, note, audit event or payload field for it, and no migration was needed. The one read the hand-over added is `GET /api/bills/by-operation/{operationId}` ([09 §2.2](09_api_spec.md)).

The code is in the Desktop: `Services/HandoverLinkReader.cs` (the link and its checks), `Services/HandoverKeyStore.cs`, `Services/HandoverPipe.cs`, `Services/HandoverUrlScheme.cs`, and `ViewModels/Handover/` (the mapping, the saving and what the operator sees).

## 1. The link

```
tallywrapper:handover?d=<D>&s=<S>
```

- `D`: the hand-over JSON below, UTF-8, base64url without padding (RFC 4648 §5).
- `S`: HMAC-SHA256 over the ASCII bytes of the `D` string exactly as it appears in the link, keyed with the hand-over key, base64url without padding (43 characters).
- The hand-over key: 32 random bytes, written as base64url without padding (43 characters). The same string is set on the sender's server and in Tally Wrapper (Settings → Hand-over), which decodes it to the 32 bytes before use.
- Accepted forms: `tallywrapper:handover?…`, `tallywrapper://handover?…` and `tallywrapper:handover/?…`, the scheme and `handover` in any case, with exactly the two parameters `d` and `s`. Anything else is refused.

## 2. The JSON, version 1

```json
{
  "v": 1,
  "issuedAt": "2026-10-25T13:30:00Z",
  "bills": [
    {
      "saveId": "0f8b6c1e-5a2d-4c7e-9b1a-3d4e5f60718a",
      "partyName": "Buyer A",
      "partyPhone": "9000000000",
      "partyAddress": null,
      "billDate": "2026-10-25",
      "payment": "Cash",
      "lines": [
        {
          "itemName": "Gold Ornaments",
          "karat": "22K",
          "pieces": 1,
          "grossWeight": 10.5,
          "lessWeight": 0.2,
          "netWeight": 10.3,
          "wastagePercent": 2.5,
          "rate": 7000,
          "labourAmount": 3090,
          "stoneValue": 1500,
          "hallmarkAmount": 45,
          "discountAmount": 0,
          "lineTotal": 72569.3
        }
      ]
    }
  ],
  "notSent": []
}
```

| Field | Meaning and limits |
|---|---|
| `v` | 1. Anything else is refused. |
| `issuedAt` | UTC, when the sender's server signed it. Refused when more than 10 minutes old or more than 5 minutes ahead of this PC's clock. |
| `bills` | 1 to 20 bills, each saved as one ordinary sale bill. |
| `saveId` | A GUID that stays the same each time the same bill is handed over again. Saved as the bill's `ClientOperationId` (`bills.CreateOperationId`). It is a random-looking version-4 GUID, like the save ID of a bill typed on a counter. Unique within the hand-over. |
| `partyName` | Required, at most 200 characters. |
| `partyPhone`, `partyAddress` | Optional; read and bounded, but not saved (see §4). |
| `billDate` | `yyyy-MM-dd`. |
| `payment` | Exactly `Cash` or `Credit and debit`. |
| `lines` | 1 to 100 sale lines. |
| `itemName` | An item of Tally Wrapper's item master, matched ignoring case and surrounding spaces. |
| `karat` | A label of the karat master, matched ignoring case and spaces (`22 K` finds `22K`), or null for a line with no stamp. |
| `pieces` | Whole pieces. |
| `grossWeight`, `lessWeight`, `netWeight` | Grams, at most 3 decimals. `lessWeight` is what came off gross (stones). |
| `wastagePercent` | The line's wastage, −100 to 1,000. Below zero for an item sold under its purity. |
| `rate` | Rupees a gram of fine (a 24K rate): the line's metal rupees divided by the sender's fine weight. Null for a line priced by the piece or with no fine weight. |
| `labourAmount`, `stoneValue`, `hallmarkAmount`, `discountAmount` | Rupees for the whole line. |
| `lineTotal` | Rupees, what the line comes to: GST inside, discount already off. Whole paise. Tally Wrapper keeps it exactly. |
| `notSent` | Save IDs of bills an earlier hand-over of the same source made that this one no longer has (a buyer removed), at most 100. Each one Tally Wrapper still holds, not voided, is reported "Not in this hand-over". Nothing is changed. |

The contract's test vector (key = the bytes 0 to 31, its `D` and `S`) is in `HandoverTestLinks` in the Desktop tests; both sides test their signer or verifier against it.

## 3. The checks

In this order; the first that fails refuses the whole hand-over, nothing in it is used, and the result dialog says why in one sentence:

1. The link's shape (§1): "This isn't a hand-over link Tally Wrapper can read."
2. This PC has a key: "This PC has no hand-over key. Set it in Settings → Hand-over."
3. The signature, compared in constant time: "This hand-over isn't signed with this PC's key."
4. The JSON's version, read first so that a newer sender's hand-over says so instead of failing the version-1 shape: "This hand-over is from a newer version. Update Tally Wrapper."
5. The JSON's shape and limits (§2): "This hand-over can't be read.", or "This hand-over has more than 20 bills." / "A bill in this hand-over has more than 100 lines."
6. Its age: "This hand-over is too old. Send it again." / "This hand-over is dated ahead of this PC's clock. Check the clock, then send it again."

The age is checked when the link arrives. Try again (§6) does not check it again, because it is the operator who asks.

## 4. Building each bill

`HandoverBillMapper` builds the bill through the Invoice tab's own line view model (`BillLineViewModel`), calculator (`BillCalculator`) and payload mapper (`InvoicePayloadMapper.BuildPayload`), so the saved bill is the bill the Invoice tab would save for the same lines: `ItemName`, `Unit`, `ItemCategory`, `PricingMode` and the Tally stock name come from the item (and the karat), `HsnCode` is the jewellery HSN, `RawJson`, `Notes`, `PartyGstin`, `PartyPhone` and `PartyAddress` are empty as on a typed bill, and the totals are `BillCalculator.BuildTotals(lineTotals, discount: 0)`. Typing the same inputs into the Invoice tab gives the same payload; `HandoverBillMapperTests` holds this.

A line fails its bill, with the reason on its row, when its item is not in the item master ("No item named “X” in Tally Wrapper’s items."), its karat is not in the karat master ("No karat “X” in Tally Wrapper’s karats."), or it has no quantity. A line with no stamp takes the item's own karat. Tally Wrapper's items have no karat field, so that is the one karat whose Tally stock item is the item's Tally stock item; with none (or more than one), the bill fails.

**The line rule.** The Invoice tab prices a gold line as `quantity × round(24K rate × purity % + wastage + labour) + extra`, with one 24K rate for the whole bill and the purity of the line's karat; a diamond line as `quantity × flat rate + extra`. The hand-over gives a line total that must stay exact, so the inputs are chosen to give it back:

1. **Quantity** is the net weight for an item whose unit is grams, and the pieces for any other unit (carats; there is no pieces unit).
2. **The bill's 24K rate** is the rate its gold lines share, else the first gold line's that has one; none when no gold line has one. The gold part of a gold line (`rate × purity`) is fixed by it, so it comes first. If a line's total is less than its gold at that rate (an item sold well under its purity, a large discount, a line priced by the piece), the bill's rate is lowered, to whole rupees, until every gold line fits; every line still keeps its total.
3. **Wastage**: the hand-over's wastage %, for an item priced with wastage; a wastage below zero counts as none.
4. **Labour**: the hand-over's labour amount as rupees a gram, for an item priced with labour.
5. Wastage and then labour are cut down to what still fits under the line's total. Everything left over (stones, hallmarking, the GST the sender added, the difference between the sender's purity and the karat master's, the discount, and the paise the rounding leaves) is the line's **Extra**.
6. A **diamond** line is one flat rate a unit, the total divided by the quantity and rounded down to the paisa; the paise left go to Extra.

No input is ever below zero (`BillValidator` refuses them), and the printed line reads as gold at the day's rate, the wastage and labour the sender charged, and the rest as Extra. Before the bill is saved it is opened as Bills → Edit opens a bill (`InvoiceEditMapper`) and every line recomputed; a bill that would not reopen to the same totals is not saved. `BillValidator` then runs on it as the server will.

## 5. Saving each bill

One after another, in the hand-over's order (`HandoverSaveWorkflow`):

- `GET /api/bills/by-operation/{saveId}`. None: create it with `ClientOperationId = saveId`, then print it.
- Found, voided or revised: reported ("Not updated", with why) and left as it is. No second bill is made.
- Found with the same content (compared field by field, whatever the decimals' trailing zeros): "Unchanged", not printed.
- Found, different, and its push to Tally not settled (`posting`, `reconciliation_required`): reported and left alone.
- Found and different: updated with `expectedRevisionId` = the revision it was found at, then printed. A `posted` bill needs an admin session: with none active, the window comes to the front with the administrator unlock, which then says "[No.] · [Party] is in Tally. Updating it needs the admin passcode.", offers **Unlock and update** and, on Esc, **Skip this bill** ("Not updated").
- A refusal from the server is that bill's reason; the next bill is still tried.
- When the server cannot be reached (no answer, or a timeout), nothing more is tried: the rest are "Not saved". A create or update that was sent without an answer is remembered, and when Try again finds the bill saved with the same content it is printed as saved.
- `notSent`: each is looked up; one that exists and is not voided is reported "Not in this hand-over", with "Void it in Bills if it’s no longer wanted." Nothing is changed.

The Bills list reloads afterwards if it is open.

## 6. Printing and what the operator sees

Each created or updated bill prints straight to the printer as a direct print after save does ([07 §1.3](07_printing_spec.md)): the remembered printer, else the Windows default, with the copy defaults of the settings. A bill that could not be printed opens in Print preview, as the direct print after save falls back.

- **All went well**: a card at the bottom right of the screen, over the other windows, that never takes the focus from the window the operator is typing in, and fades after about 5 seconds (held while the pointer is on it). Its title is "2 bills saved and printed", "Bill saved and printed" or "1 bill updated and printed" (with rows for the unchanged ones); a row per bill with the invoice number, the party, the ₹ total and the payment or what happened to it. A click on it, or **Open Bills**, brings Tally Wrapper to the front on Bills.
- **Anything else** (a bill not saved, the server not answering, a bill left out of this hand-over, a skipped admin update, a refused link): Tally Wrapper's window comes to the front with the result dialog: a row per bill with a result chip and a reason line, **Try again** (Enter) when the server could not be reached, **Open Bills** (Ctrl+2) and **Close** (Esc). Try again runs the same hand-over again; the bills saved by the first run come back "Unchanged".

Windows lets a program take the foreground only at certain moments. A later start of the app passes the permission the browser gave it to the running copy (`AllowSetForegroundWindow`); when Windows still refuses, the taskbar button flashes.

## 7. Receiving the link

- **The scheme.** At every start the published app registers `tallywrapper` for the signed-in Windows user: `HKCU\Software\Classes\tallywrapper` with `URL Protocol` and `shell\open\command` = `"<this exe>" "%1"`, rewritten when it points elsewhere. Only the published app does it, so that a test, a scratch run or a development build never takes the links: it must be a Release build, in the Production environment (`DOTNET_ENVIRONMENT` unset or `Production`; `tools\run-scratch.ps1` and the VS Code launches set Development), with no `SHOWROOM_BILLING_APPDATA` or `SHOWROOM_BILLING_LOCALAPPDATA` override (the tests and `run-scratch.ps1` set them), running as a single-file exe (what `tools\publish-prod.ps1` makes; a build's `bin` folder is not). `HandoverUrlScheme` holds the guard.
- **One copy takes it.** A start whose argument is a `tallywrapper:` link first offers the link to a copy already running, over a named pipe that only the same Windows user in the same session can open (`PipeOptions.CurrentUserOnly`; the name carries the user, the session, the environment, the build and the settings folder, so a scratch or development copy never answers for the real one). If one takes it, the new start exits at once. With none running, the app starts normally and handles the link once it has started and loaded its settings and masters (if the server did not answer at start-up, it asks for the settings once more first). A start without a link is unchanged: there is no single-instance rule, so a second plain start still opens a second window.
- Links that arrive while another hand-over is being handled wait their turn. A hand-over's dialogs replace a dialog that is open; the Invoice tab's form is never touched.
- The log records each hand-over's counts and any refusal's reason, never the link, the key or a party's name.

## 8. The key

Settings → **Hand-over**, in the **This PC only** group, shows whether this PC has a key (as dots, never the key), with **Change…** (a small dialog to paste a key: 43 characters of base64url that decode to 32 bytes, else a plain error) and **Remove** (asks first). With no key, hand-over is off and a link gets the result dialog saying so.

The key is kept in `%LOCALAPPDATA%\ShowroomBilling\handover-key.dat` (`AppPaths.LocalDirectory`), protected with Windows DPAPI for the signed-in Windows user. It is never in the cloud settings, the database or a log: it is a secret this PC shares with the sender on this PC, so another counter without the sender has no use for it, and a key that left this PC could forge hand-overs. See [14](14_settings_storage_contract.md). To change it, set the new key on the sender's server and paste the same key here.

## 9. Testing

- `HandoverLinkReaderTests`: the contract's vector verifies; a changed byte, another key, an old or future `issuedAt`, a `v` other than 1, bad shapes and over-limit counts are refused.
- `HandoverBillMapperTests`: the line total is exact and the Invoice tab's recompute reproduces it, for gold, piece-priced and diamond items, with odd paise and wastage below zero; no input below zero; unknown item and karat; typing the same inputs into the Invoice tab gives the same payload.
- `HandoverSaveWorkflowTests`, `HandoverCoordinatorTests`, `HandoverReportTests`: create, unchanged, update, posted with the admin passcode skipped and given, voided, revised, not settled, `notSent`, an unreachable server and Try again.
- `HandoverStartupTests`: the registration guard, the registry keys (written under a scratch key), the pipe.
- `BillByOperationContractTests`: the API read, found and not found.
- `HandoverViewSnapshotTests` lays out the toast, each case of the result dialog, the admin unlock, the key dialog and the settings section; with `TALLYWRAPPER_VIEW_SNAPSHOTS` set they are saved as `Handover-*.png`.

What only a person can check: that a link from the browser starts or reaches the app, that the card shows without taking the focus from the browser, that the window comes to the front for the dialogs, and that the bills print.
