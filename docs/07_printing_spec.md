# Printing Specification

This document freezes operator-visible printing behavior for Tally Wrapper.

Implementation direction:

- WPF desktop host
- QuestPDF as canonical document composition engine

Operator-visible behavior must preserve what matters from the current system.

---

## 1. Printing modes to preserve

### 1.1 Estimate print

Current behavior to preserve:

- estimate print is launched from the sales/invoice screen
- it uses unsaved working form data
- it opens a preview dialog
- it is not the same as final stored-bill print

Tally Wrapper rule:

- keep estimate as a preview-first workflow from the draft screen
- do not silently turn it into final invoice print

### 1.2 Final invoice print

Current behavior to preserve:

- history/bills print works on saved bills
- single bill print opens preview
- batch bill print opens merged preview

Tally Wrapper rule:

- keep final invoice print separate from estimate print
- keep bills/history print separate from draft estimate print

### 1.3 Preview vs direct print

Current behavior to preserve:

- operators can preview invoice before printing
- after successful save/update there is a direct-print fast path
- direct print bypasses visible preview dialog

Tally Wrapper rule:

- keep both preview and direct-print modes
- direct print remains an explicit choice, not the only path

Tally Wrapper implementation:

- preview dialog has a "Direct print after save (skip preview)" checkbox stored in
  the local `IPrintPreferencesStore`
- when enabled and a printer is remembered (or a Windows default printer exists),
  post-save jobs go straight to the printer with the copy defaults and clamped
  layout from the cloud settings; the invoice screen clears immediately
- when direct print fails (no remembered/default printer, or the print queue
  rejects the job) the flow falls back to preview so the operator sees the issue

### 1.4 PDF export

Current behavior to preserve:

- preview dialog supports PDF export
- last PDF directory is remembered locally

Tally Wrapper rule:

- keep PDF export from preview
- remember the last save directory locally per workstation/user profile

---

## 2. Copy modes

Current behavior to preserve:

- `Original for Recipient`
- `Duplicate for Transporter`
- `Triplicate for Supplier`

Rules:

- preview must allow toggling copy modes for saved bill print
- if all copy toggles are unchecked, `Original` is auto-restored
- copy defaults come from saved settings

Tally Wrapper implementation note:

- QuestPDF should render all selected copies from one canonical template
- operator semantics stay the same even if the rendering engine changes

---

## 3. Batch print behavior

Current behavior to preserve:

- selecting multiple bills and printing creates one merged preview
- batch print is preview-based, not direct print

Tally Wrapper rule:

- keep merged preview behavior for batch print
- merged output should avoid duplicate asset loading behavior where practical

---

## 4. Printer selection persistence

Current behavior to preserve:

- user can pick a printer from preview dialog
- selected printer name is remembered locally
- later preview/direct-print flows reuse that printer selection when appropriate

Tally Wrapper rule:

- preserve local remembered printer selection
- printer persistence is local workstation preference, not cloud-shared business data

---

## 5. Layout assets and document controls

### 5.1 Assets

Current behavior to preserve:

- optional logo file
- optional authorized signature file
- clear/browse actions
- assets affect rendered invoice output

Tally Wrapper rule:

- preserve logo/signature usage
- preserve ability to clear or replace assets

### 5.2 Layout controls

Current behavior to preserve:

- editable margins
- editable font sizes
- logo width/height/position
- signature width/height/position
- live layout calibration editor

Tally Wrapper rule:

- preserve operator/admin ability to calibrate output
- the rendering engine may change, but calibration controls must survive materially

Tally Wrapper clamp ranges (ported from V1 `core/print_layout.py`; enforced by
`PrintLayoutOptions.Clamped()` before rendering):

- per-side page margin: `0 mm` – `25 mm`
- invoice font size: `8` – `18`
- terms font size: `7` – `16`
- logo size: within `~10.6 × 6.4 mm` minimum and the `~63.5 × 22.2 mm` slot;
  offset is clamped so size + offset never exceeds the slot bounds
- signature size: within `~15.9 × 6.4 mm` minimum and the `~58.2 × 19.6 mm` slot;
  offset clamped the same way

Tally Wrapper default layout (operator-blank install): margins `10 / 10 / 10 / 12 mm`,
invoice font `11`, terms font `9`, original copy on, duplicate/triplicate off.

### 5.4 Live preview in Settings

The Settings screen hosts a docked live print preview pane on the right for the
**Invoice** and **Print Layout** sections only.

- Renders one `Original` copy of a canned sample bill (two lines, one exercising
  every optional column) via the same `BillPrintRenderer`/QuestPDF pipeline used
  for the real print dialog.
- Reacts to unsaved edits with a 300 ms debounce. Source observations:
  - `SettingsDraft` — company, bank, terms, invoice/terms font sizes
  - `PrintLayoutViewModel` — margins, logo/signature placements, locally uploaded
    bytes (`PendingLogoBytes` / `PendingSignatureBytes`)
- Copy-default toggles are not reflected in the preview; they govern real print
  only. A header note in the pane states this.
- Uses a tolerant `SettingsDraft.BuildPrintSettingsSnapshot()` that accepts
  half-typed state (blank company name, unparseable font size) by falling back to
  V1 defaults, so the preview stays usable mid-edit.
- Async results are gated by a render-generation counter: stale renders that
  finish after a newer change is enqueued are discarded without touching the
  preview. Asset downloads are gated by an independent asset-generation counter
  so a late server download cannot overwrite newer local upload bytes.
- Refreshes are suppressed while `SettingsViewModel.IsLoading`,
  `SettingsViewModel.IsSaving`, or `PrintLayoutViewModel.IsBusy` is true; one
  trailing-edge refresh fires when bulk-update completes.
- Rendered at 96 DPI; the pane is a fixed 420 px column.

### 5.3 What can improve internally

Allowed improvement:

- replace HTML/WebEngine rendering with WPF + QuestPDF composition and preview plumbing
- improve print fidelity and PDF quality
- improve preview responsiveness

Not allowed to regress:

- estimate vs final distinction
- direct-print option
- copy toggles
- PDF export
- printer memory
- layout/asset settings coverage

---

## 6. Visual/output requirements

### 6.1 Page

- A4 portrait invoice output
- predictable margins under operator-configured layout
- stable totals/footer placement

### 6.2 Content

The document must preserve the current commercial content categories:

- company details
- GST details
- bank details
- invoice numbering
- line items
- quantities, rates, amounts
- totals
- terms and conditions
- optional copy labels
- optional logo/signature

### 6.3 Consistency

Tally Wrapper should preserve operator-visible content structure, but exact pixel-for-pixel parity with the old HTML print engine is not required.

The following **must remain semantically identical**:

- which document type is being printed
- which data fields appear
- copy mode semantics
- direct-print vs preview availability
- whether a draft estimate can be printed before saving

### 6.4 Tally Wrapper layout blocks (QuestPDF)

The `BillDocument` composer in `src/ShowroomBilling.Printing` renders, per copy,
a single A4 page with the following blocks (top-to-bottom):

1. Copy label, top-right (`Original for Recipient` / `Duplicate for Transporter` /
   `Triplicate for Supplier`) — matches V1 wording.
2. Centered logo slot + `TAX INVOICE` banner with rules above and below.
3. Two-column band: company details (name bold, GSTIN / Phone / Address rows) on
   the left; Invoice No. + Date, then `Bill To` party details on the right. The
   `Bill To` headline is the operator's PartyName; if that is blank, it falls
   back to the normalized payment-mode label (`Cash` / `Credit and debit`),
   mirroring V1's `sales_tab` auto-default. When PartyName *is* populated, a
   small `Payment: {mode}` line is rendered below the address/GSTIN/phone block
   so the payment context is preserved on print — V1 dropped this signal once
   the operator typed a real customer name; Tally Wrapper keeps it.
4. Line-item table. Columns, conditional where noted:
   `#`, `Description`, `HSN` (defaults to `711319` when blank),
   `Gross Wt` + `Less Wt` (shown when any line has a non-zero less weight),
   `Net Wt`, `Purity`, `Making` (wastage % + labour per gram),
   `Extra` (shown when any line has extra charges),
   `Rate/g`, `Amount` (GST-inclusive, allocated proportionally per line).
5. Right-aligned summary: `Items Total (Incl. GST)`, optional `Discount`,
   optional `Round Off`, bold `Total Amount (Incl. GST)`.
6. GST breakup box: Taxable Value / `CGST @ 1.5%` / `SGST @ 1.5%` / Total GST Included.
   (CGST/SGST percentages are hardcoded to mirror the V1 default; making them
   configurable is tracked separately.)
7. Bank details box (rendered only when any of bank name / account / IFSC / UPI
   is populated on the company profile).
8. Terms & Conditions block and signature block (`For {company}` + signature slot
   + `Authorised Signatory`).

**Dynamic page fill**: the bordered invoice box stretches to the A4 bottom, and a
dynamic spacer sits between block 4 (line-item table) and block 5 (summary). On
short bills the spacer expands to push summary / GST / bank / terms / signature
to the page bottom — producing the same full-page look V1 had. On bills where
content already fills or exceeds the page, the spacer collapses to zero and
QuestPDF paginates naturally, so the fill cannot cause overflow. Always on; no
user toggle.

---

## 7. Local persistence allowed for printing

Allowed local-only persistence:

- last printer name
- last PDF directory
- local preview/UI preferences if needed

This does not violate the no local durable business storage rule because it is workstation UX state, not business truth.

---

## 8. Preserve vs improve

### Must remain operator-identical

- estimate print exists and is preview-first
- final saved-bill print exists separately
- direct print exists as explicit path after save
- preview supports PDF export
- copy toggles exist
- batch print creates merged preview
- printer selection is remembered
- logo/signature and layout controls exist

### Can improve in Tally Wrapper

- rendering engine internals
- preview shell UX
- PDF quality
- printer abstraction layer
- internal document model and template maintainability
