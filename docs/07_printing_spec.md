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
  layout from the cloud settings plus the locally remembered printer settings;
  the invoice screen clears immediately
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

The print preview dialog also exposes local printer-job settings:

- Duplex: printer default / one-sided / both sides long edge / both sides short edge
- Color: printer default / color / black and white
- Collation: printer default / collated / uncollated

These settings are queried from the selected printer's `PrintCapabilities`, unsupported
options are omitted, and the selected values are merged/validated into a WPF
`PrintTicket` before dispatch. If capabilities cannot be read, the dialog falls
back to printer defaults. The values are stored only in the local
`print-preferences.json` file, are restored visibly when the preview reopens,
and are reused by direct-print-after-save.

---

## 5. Layout assets and document controls

### 5.1 Assets

Current behavior to preserve:

- optional logo file
- optional authorized signature file
- optional page watermark file
- clear/browse actions
- assets affect rendered invoice output

Tally Wrapper rule:

- preserve logo/signature usage and support a calibrated watermark
- preserve ability to clear or replace assets
- accept PNG/JPEG assets up to 2 MiB; a missing/deleted watermark or zero opacity
  must never block printing

### 5.2 Layout controls

Current behavior to preserve:

- editable margins
- editable font sizes
- logo width/height/position
- signature width/height/position
- watermark X/Y/width/height/opacity using full-page A4 coordinates
- ordered invoice sections with optional visibility and external before/after spacing
- compact/standard/comfortable density, invoice-border thickness, and a
  bottom-pinned trailing-section boundary
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
  offset clamped the same way; the signatory rule is the configured image width
  plus `4 mm` total overhang, capped to the slot width
- watermark: `X 0..21 cm`, `Y 0..29.7 cm`, width `0.1..21 cm`, height
  `0.1..29.7 cm`, opacity `0..100%`; the configured box must remain inside A4
- section before/after spacing: `0..20 mm`
- invoice border: `0..4 pt`

Tally Wrapper default layout (operator-blank install): margins `10 / 10 / 10 / 12 mm`,
invoice font `11`, terms font `9`, standard density, `1 pt` invoice border,
original copy on, duplicate/triplicate off. All sections use the historical
order and are visible; optional sections naturally omit themselves when their
content/asset is absent. Bottom pinning starts at GST Breakup.

### 5.3 Structured page-flow designer

The shared `PrintPageLayoutSettings` contract stores every known section key
exactly once. Unknown, duplicate, or missing keys are rejected. Copy Label,
Invoice Title, Company/Party, Items, Totals, and GST are mandatory; Logo, Notes,
Bank, Terms, and Signature may be hidden.

Rows can be reordered by native WPF drag/drop or the keyboard-accessible Move Up
and Move Down buttons. “Pin bottom from here” stores a boundary key: that visible
section and all visible sections after it form one contiguous trailing group.
Reordering therefore recalculates membership from the boundary instead of
persisting invalid per-row pin flags. “Reset defaults” restores the historical
order, visibility, standard density, `1 pt` border, zero external spacing, and
the GST boundary.

### 5.4 Live preview in Settings

The Settings screen hosts a docked live print preview pane on the right for the
**Invoice** and **Print Layout** sections only.

- Renders one `Original` copy of a canned sample bill (two lines, one exercising
  every optional column) via the same `BillPrintRenderer`/QuestPDF pipeline used
  for the real print dialog.
- Reacts to unsaved edits with a 300 ms debounce. Source observations:
  - `SettingsDraft` — company, bank, terms, invoice/terms font sizes
  - `PrintLayoutViewModel` — margins, logo/signature/watermark placements,
    section order/visibility/spacing, density, border and bottom pin; locally
    uploaded bytes (`PendingLogoBytes`, `PendingSignatureBytes`,
    `PendingWatermarkBytes`)
- Copy-default toggles are not reflected in the preview; they govern real print
  only. A header note in the pane states this.
- Uses a tolerant `SettingsDraft.BuildPrintSettingsSnapshot()` that accepts
  half-typed state (blank company name, unparseable font size) by falling back to
  V1 defaults, so the preview stays usable mid-edit.
- Async results are gated by a render-generation counter: stale renders that
  finish after a newer change is enqueued are discarded without touching the
  preview. Asset downloads are gated by an independent asset-generation counter
  so a late server download cannot overwrite newer local upload bytes.
- If the connected server rejects a watermark upload (for example, an older
  server deployment that does not yet accept the `watermark` asset kind), the
  selected bytes remain visible in the live preview and the UI labels them as a
  local-only preview. Save is blocked until the matching server is installed and
  the file is browsed again, preventing a watermark from being silently omitted.
- Refreshes are suppressed while `SettingsViewModel.IsLoading`,
  `SettingsViewModel.IsSaving`, or `PrintLayoutViewModel.IsBusy` is true; one
  trailing-edge refresh fires when bulk-update completes.
- Rendered at 120 DPI; the pane starts at 420 px and participates in the
  Settings split layout.

### 5.5 What can improve internally

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

- A4 portrait invoice output; printer-job settings do not change invoice page
  orientation or paper size
- the preview opens fitted to the whole rendered page after rendering completes;
  manual zoom remains available without a fitted view overwriting the saved zoom
  preference
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
- optional watermark behind all invoice content

### 6.3 Consistency

Tally Wrapper should preserve operator-visible content structure, but exact pixel-for-pixel parity with the old HTML print engine is not required.

The following **must remain semantically identical**:

- which document type is being printed
- which data fields appear
- copy mode semantics
- direct-print vs preview availability
- whether a draft estimate can be printed before saving

### 6.4 Tally Wrapper layout blocks (QuestPDF)

The `BillDocument` composer in `src/ShowroomBilling.Printing` renders A4 portrait
pages through section dispatchers. The default order remains:

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
   `#`, `Description`, `Gross Wt` + `Less Wt` (shown when any line has a non-zero less weight),
   `Net Wt`, `Purity`, `Making` (wastage % + labour per gram),
   `Extra` (shown when any line has extra charges),
   `Rate/g`, `Amount` (GST-inclusive, allocated proportionally per line).
   `Description` is the only item-table column allowed to wrap; all other
   item-table headers and values are kept to a single line. Weight and making
   values use compact print formatting (`22.345g`, `12.5%+150/g`) to avoid
   space-based wraps in the fixed-width columns. `Making` is narrower than the
   old wide column but still fits the rare percentage+labour value.
5. Right-aligned summary: `Items Total (Incl. GST)`, optional `Discount`,
   optional `Round Off`, bold `Total Amount (Incl. GST)`.
6. GST breakup box: HSN Code (defaults to `711319` when blank) / Taxable Value /
   `CGST @ 1.5%` / `SGST @ 1.5%` / Total GST.
   (CGST/SGST percentages are hardcoded to mirror the V1 default; making them
   configurable is tracked separately.)
7. Bank details box (rendered only when any of bank name / account / IFSC / UPI
   is populated on the company profile).
8. Terms & Conditions block and signature block (`For {company}` + signature slot
   + `Authorised Signatory`).

Each configured section is dispatched once per copy. Density scales internal
vertical padding (`0.75×`, `1×`, `1.25×`); per-section spacing is applied only
outside the section. The unpinned sequence flows normally, followed by a
collapsible spacer and the bottom-aligned pinned sequence. On content-heavy
bills the spacer collapses and QuestPDF paginates naturally; the trailing group
appears once at the end of the copy.

The watermark is separate from section flow. QuestPDF's full-page background
slot repeats it on every physical page behind invoice content. A single
opacity-bearing inline SVG wrapper embeds the PNG/JPEG bytes once per document
and preserves the source aspect ratio inside the configured bounding box.

---

## 7. Local persistence allowed for printing

Allowed local-only persistence:

- last printer name
- duplex / color / collation printer-job settings
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
- logo/signature/watermark and structured layout controls exist

### Can improve in Tally Wrapper

- rendering engine internals
- preview shell UX
- PDF quality
- printer abstraction layer
- internal document model and template maintainability
