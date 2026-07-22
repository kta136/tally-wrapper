# UI Design Reference

## 1. Purpose

The canonical visual and interaction target for the Tally Wrapper desktop shell is the prototype bundled under [docs/design/](design/). It is an HTML/CSS/React hi-fi mockup exported from Claude Design; it is **not** production code.

This document is the contract between the implementation and the design bundle: it tells the implementer **what the final UI should look and feel like**.

## 2. What to read before implementing a UI slice

1. [design/Tally Wrapper.html](design/Tally%20Wrapper.html) — entry point; pulls in the component files below.
2. [design/app/styles.css](design/app/styles.css) — the full design system (tokens, components, density toggle, focus ring). Treat this as the source of truth for colors, spacing, typography, and states.
3. [design/app/shell.jsx](design/app/shell.jsx) — titlebar, nav, F-key strip, status bar.
4. [design/app/invoice.jsx](design/app/invoice.jsx) — invoice screen.
5. [design/app/screens.jsx](design/app/screens.jsx) — bills + settings screens.
6. [design/app/dialogs.jsx](design/app/dialogs.jsx) — bill details, print preview, post-save, admin unlock, shortcuts, danger confirm.

The README in the original bundle ([handoff notes](design/)) says: *recreate pixel-perfectly in whatever technology fits the target codebase — match the visual output; don't copy the prototype's internal structure*. For us the target is **WPF**.

## 3. Design-system tokens (port to WPF ResourceDictionary)

### 3.1 Accent and neutrals

| Token | Value | Use |
|---|---|---|
| `--accent` | `oklch(45% 0.14 258)` — deep indigo-blue | primary buttons, focus ring, active tab underline |
| `--accent-hover` | `oklch(40% 0.14 258)` | primary button hover |
| `--accent-pressed` | `oklch(35% 0.14 258)` | primary button pressed |
| `--accent-soft` | `oklch(94% 0.03 258)` | focused table row, selected settings nav item |
| `--accent-ring` | `oklch(55% 0.16 258 / 0.45)` | outer focus glow on inputs |
| `--bg` | `oklch(98.5% 0.003 260)` | window background |
| `--bg-panel` | `oklch(100% 0 0)` | panels, cards, dialogs |
| `--bg-sunken` | `oklch(96.5% 0.004 260)` | status bar, F-key strip, table header, nav sidebar |
| `--bg-hover` | `oklch(95.5% 0.01 258)` | row hover |
| `--bg-selected` | `oklch(93% 0.03 258)` | selected row |
| `--border` | `oklch(88% 0.006 260)` | panel borders, dividers between regions |
| `--border-strong` | `oklch(78% 0.008 260)` | buttons, dialog outlines |
| `--divider` | `oklch(92% 0.005 260)` | table row separators, sub-section dividers |
| `--ink` | `oklch(22% 0.01 260)` | body text |
| `--ink-muted` | `oklch(45% 0.01 260)` | secondary text, labels |
| `--ink-soft` | `oklch(58% 0.008 260)` | tertiary text |
| `--ink-disabled` | `oklch(72% 0.006 260)` | disabled controls |

### 3.2 Status colors (matched chroma)

| Token | Value | Use |
|---|---|---|
| `--ok` | `oklch(50% 0.12 150)` | posted chip, healthy dot |
| `--warn` | `oklch(58% 0.14 75)` | pending chip, degraded dot, warn banner |
| `--err` | `oklch(50% 0.17 28)` | failed chip, limited banner, danger buttons |
| `--info` | `oklch(52% 0.11 245)` | info chip |

Each has a `-soft` companion for fills (`--ok-soft`, `--warn-soft`, `--err-soft`, `--info-soft`) and a matching dot style with a 2px glow halo.

The Invoice screen's **edit-mode banner** uses a dedicated palette outside the standard status set so we can tune banner saturation independently of inline chips. WPF brushes: `EditBannerSoftBrush` (`#FFFEF3C7`), `EditBannerBorderBrush` (`#FFF0C674`), `EditBannerInkBrush` (`#FF7A4F01`). Mirrors the design bundle's `#fef3c7 / #f0c674 / #7a4f01`.

### 3.3 Density (user-switchable at runtime, compact default)

| Token | Compact (default) | Spacious |
|---|---|---|
| `--row-h` | `28px` | `36px` |
| `--input-h` / `--btn-h` | `28px` | `32px` |
| `--cell-px` | `10px` | `14px` |
| `--font-ui` | `12.5px` | `13px` |
| `--radius` | `3px` | `4px` |
| `--radius-lg` | `5px` | `8px` |

Density is a settings-level toggle, not a Tweaks-panel affordance in production (see §9).

### 3.4 Typography

- UI: **Inter** (weights 400/500/600/700) with Segoe UI Variable fallback. `font-feature-settings: 'cv11', 'ss01', 'tnum'`, `line-height: 1.35`.
- Numeric columns, invoice numbers, kbd hints, timestamps: **JetBrains Mono** (weights 400/500/600) with Cascadia Mono fallback. `font-variant-numeric: tabular-nums` everywhere numbers are compared vertically.
- Label size `11.5px` uppercase `letter-spacing: 0.03em`; section title `10.5px` uppercase `letter-spacing: 0.06em`.

### 3.5 Focus ring (keyboard-first, non-negotiable)

- `outline: 2px solid var(--accent)` with `outline-offset: 1px` on every interactive element's `:focus-visible`.
- Inputs additionally get `border-color: var(--accent)` and an outer `outline: 2px solid var(--accent-ring)` (with `outline-offset: -1px`) to keep the ring visible against dense table cells.
- This is a shell done-criterion — non-negotiable on every interactive element.

### 3.6 Chrome and structure

- App frame is `grid-template-rows: titlebar | nav/banner | main | status` with `height: 100vh`, `overflow: hidden`.
- Titlebar is 32px, sunken-white, with brand mark (indigo square, monospace "S") + centered context strip (Company · Counter · Operator) + window buttons.
- Nav is 36px (compact) / 42px (spacious), tabs get a 2px `--accent` underline when active and a 6×6 SVG icon to the left of the label.
- Status bar is 22px, monospace 10.5px, sunken-white.
- F-key strip sits just above the status bar (22px, same treatment) and is **context-sensitive** per screen — see [shell.jsx FKeyStrip](design/app/shell.jsx).

## 4. Component inventory (classes to port)

| CSS class | Purpose | Design file |
|---|---|---|
| `.titlebar` / `.brand` / `.win-btns` | Windows-style title bar | styles.css, shell.jsx |
| `.navbar` / `.nav-item` / `.nav-spacer` / `.nav-right` | primary tabs + right-side health cluster | styles.css, shell.jsx |
| `.statusbar` / `.sep` / `.chip` | bottom status strip | styles.css, shell.jsx |
| `.banner.warn` / `.banner.err` | degraded / limited mode banners | styles.css, app.jsx |
| `.btn` (+ `.primary` / `.danger` / `.ghost` / `.sm`) / `.btn-group` | buttons | styles.css |
| `.kbd` | inline keyboard hint | styles.css |
| `.input` / `.select` / `.textarea` / `.field` | form controls | styles.css |
| `.chip` (+ `.ok` / `.warn` / `.err` / `.info` / `.accent` / `.outline`) | status chips | styles.css |
| `.dt` (`thead th` / `tbody td` / `.focus-row` / `.selected` / `.num`) | dense data table | styles.css |
| `.panel` / `.panel-header` / `.panel-body` | sectioned panel | styles.css |
| `.tile` (+ `.ok` / `.warn` / `.err` / `.info`) | bill-summary tile | styles.css |
| `.dialog` / `.dialog-head` / `.dialog-body` / `.dialog-foot` / `.scrim` | modal dialog | styles.css, dialogs.jsx |
| `AdminSectionPanel` / `AdminSectionTitle` / `AdminFieldLabel` / `AdminDataGrid` / `AdminBannerErr`·`Warn`·`Info` / `StateChipLocked`·`Unlocked` | admin sections | [Resources/Styles.xaml](../src/ShowroomBilling.Desktop/Resources/Styles.xaml) — shared by `AdminUnlockDialog` and Settings › Admin tab |
| `.entry-row` / `.rcell` | in-table editable cell | styles.css, invoice.jsx |
| `.picker` / `.picker-item` | side item picker | styles.css, invoice.jsx |
| `.settings-nav` / `.it` / `.group` | settings sidebar | styles.css, screens.jsx |
| `.limited-card` | limited-mode recovery card | styles.css, app.jsx |
| `.timeline` / `.node` / `.bullet` | sync timeline | styles.css, dialogs.jsx |
| `.preview-paper` / `.preview-stamp` | thermal-print preview | styles.css, dialogs.jsx |
| `.dot` (+ `.ok` / `.warn` / `.err` / `.pulse`) | status dot with halo | styles.css |
| `.toast` | transient save confirmation | styles.css, invoice.jsx |

Every class above has a direct WPF translation — most become a `Style` keyed in a `ResourceDictionary`. The design tokens in §3 become `SolidColorBrush` / `Thickness` / `double` resources so a later Spacious/Compact switch is a single `MergedDictionaries` swap.

## 5. Screens

| Screen | Design file |
|---|---|
| App shell (titlebar, banners, nav, F-key strip, status bar, health cluster, shortcuts dialog, system-health dialog) | [app.jsx](design/app/app.jsx), [shell.jsx](design/app/shell.jsx) |
| Limited-mode view | [app.jsx LimitedModeView](design/app/app.jsx) |
| Invoice screen (header grid, line-entry table with autocomplete + locked tab order, totals column, side picker, toolbar, actions bar, post-save dialog) | [invoice.jsx](design/app/invoice.jsx), [dialogs.jsx PostSaveDialog](design/app/dialogs.jsx) |
| Bills screen (filter bar, batch action bar, table with status chips + context menu + multi-select, footer) | [screens.jsx BillsScreen](design/app/screens.jsx) |
| Bill details dialog (summary, commercial breakdown, numbering, sync timeline) | [dialogs.jsx BillDetailsDialog](design/app/dialogs.jsx) |
| Print preview dialog (estimate/final toggle, watermark, copy checkboxes, thermal mock) | [dialogs.jsx PrintPreviewDialog](design/app/dialogs.jsx) |
| Settings screen (sectioned nav, Connection / Invoice Content / Advanced with full treatment; Numbering / Ledgers / Items / Karat / Invoice Layout / Company as styled scaffolds) | [screens.jsx SettingsScreen](design/app/screens.jsx) |
| Admin unlock + danger confirm dialogs | [dialogs.jsx](design/app/dialogs.jsx) |
| Shortcuts help (`?` overlay) | [dialogs.jsx ShortcutsDialog](design/app/dialogs.jsx) |

## 6. Keyboard-first rules (design-enforced, port verbatim)

Global (registered in the app shell):

- `Ctrl+1` Invoice tab · `Ctrl+2` Bills tab · `Ctrl+3` Settings tab
- `?` open shortcuts overlay
- `Esc` close top-most dialog
- `F9` print estimate (Invoice screen active)
- `Ctrl+S` saves the active screen: Save Bill on Invoice, Save in Settings.

Per-screen F-key strip content (from [shell.jsx FKeyStrip](design/app/shell.jsx)):

- Invoice: `F2 Edit Rate · F3 Item Picker · F4 Party · F9 Est. Print · Ctrl+S Save Bill · Ctrl+N New Row · Ctrl+Del Remove Row · Esc Cancel · ? Shortcuts`
- Bills: `Enter Details · Button Push to Tally · Ctrl+R Retry Push · Ctrl+Shift+R Repost to Tally · Ctrl+Shift+E Edit · Ctrl+P Print · Search field · Shift+Del Delete · ? Shortcuts`
- Settings: `Ctrl+S Save · Esc Cancel · Tab Next Field · ? Shortcuts`

Invoice line-entry tab-order (from [invoice.jsx handleCellKey](design/app/invoice.jsx)):

- Row cell order: `name → qty → wt → unit → karat → wastage → labour → extra`
- `Tab` advances one cell; at the last cell on a row it wraps to cell 0 of the next row (creating a new row if needed).
- `Shift+Tab` reverses through the same order.
- `Enter` advances one cell (same order as `Tab`); `Shift+Enter` reverses.
- Autocomplete on the name cell: `↑↓` selects, `Enter` picks (and jumps to qty), `Esc` dismisses. Dropdown shows up to 7 matches.

Invoice end-to-end `Enter` flow (header → lines → save → print):

1. Cursor lands on **24kt Rate** when the Invoice screen becomes visible.
2. `Enter` on 24kt Rate → **Party** field.
3. `Enter` on Party → **Item** cell of the first row.
4. Inside a row, `Enter` advances cell-by-cell. When `Enter` is pressed on the **Item** cell of an empty row, focus jumps to the **Save Bill** button.
5. `Enter` on Save Bill triggers save; on success the Print Preview dialog opens with focus on its **Print** button.
6. `Enter` in Print Preview prints. `Esc` closes the dialog.

## 7. System-state visuals (the thing that differs from a normal WPF app)

Two operating states, driven by whether the API is reachable. Tally itself is only touched synchronously when the operator clicks Push or Refresh, so there is no live "Tally health" signal — the Tally dot stays neutral until the next operator action tells us otherwise.

| State | Banner | Titlebar treatment | Nav/health cluster |
|---|---|---|---|
| `healthy` | none | default | green dot on Cloud; neutral dot on Tally |
| `limited` | err banner: *"Limited mode — cloud unavailable."* | titlebar background = warn-soft | err dot on Cloud; err dot on Tally (API can't reach Tally either) |

In `limited`, Invoice and Bills are hidden. The main region shows the `LimitedModeView` recovery card; Settings remains available so an operator can retry health checks or change Database recovery mode.

The design's Tweaks panel lets a reviewer toggle system state for preview purposes. In production the state is derived from real API health signals.

## 8. Sample data

The mockup ships realistic Indian jewellery sample data in [design/app/data.js](design/app/data.js):

- Items: 22kt gold chain, 22kt bangle set, 18kt diamond stud, 22kt Lakshmi pendant, 22kt mangalsutra, 24kt gold coin, 92.5 silver anklet, etc.
- Bills: `SR/25-26/0142` style numbering, mix of posted/pending/failed/voided with realistic amounts and error strings ("Voucher Type 'GST Sales' not found", "Ledger 'Vijaya Stores' missing in Tally").
- Parties, status-bar rates (24kt ₹7,780/g, 22kt ₹7,125/g, 18kt ₹5,830/g).
- `fmtINR` helper implements Indian grouping (`1,23,456.78`).

Use this as the shape of every placeholder / mock state while real API wiring lands.

## 9. Deliberate departures from the prototype

The mockup contains a few review-only affordances that we do **not** ship to operators:

- **Tweaks panel** (bottom-right gear): density/system-state/hints/accent switcher. Review-only. In production:
  - density lives in Settings (user UX preference, workstation-local; see [14_settings_storage_contract.md](14_settings_storage_contract.md));
  - system state is derived from real health signals;
  - keyboard hints stay on (they are a core affordance, not decorative);
  - accent is fixed at indigo (we do not expose accent theming).
- **Window buttons in the titlebar** (min/max/close as painted SVGs): defer until we decide custom-chrome vs native-chrome. If we keep native WPF chrome, the painted title bar goes with it and the brand/context strip becomes a thin header band below it.
- **Tab-switcher "keyboard hints on/off"**: hints stay on.
- **Title-bar "Counter X" strip**: the prototype shows `Company · Counter · Operator` in the centered context strip. WPF ships `Company · Operator` only — we don't model multiple counters per workstation. The workstation identifier (`WS: COUNTER-01`) lives in the status bar instead.
- **Settings → Database panel**: the prototype shows a Bootstrap API endpoint / Tenant Slug / Workstation Token form with a snapshot freshness table — that's a stylized cloud-onboarding mockup that doesn't reflect the real desktop architecture. WPF instead uses a compact `Component / Mode / Details / Status` table for the configured Desktop → API → PostgreSQL route and explicitly states that the Desktop never connects to PostgreSQL directly. The status column reports configuration (`OK` / `ACTIVE` / `CONFIGURED`), not live reachability; Refresh and Test remain the explicit health actions. API-location controls are separate from the LocalEmbedded database controls. In Server mode, the workstation's local override is labelled **NOT IN USE** and the operator is directed to the server tray for the active database. The panel retains Test / Save-override / Restart-API actions, a masked connection value, source/environment/restart state, encrypted override path, and copy actions for non-secret endpoints and paths. Settings navigation uses the Windows-native `Segoe Fluent Icons` / `Segoe MDL2 Assets` icon font while preserving the dynamic admin-only entry. See [DatabaseSettingsSectionView.xaml](../src/ShowroomBilling.Desktop/Views/Settings/DatabaseSettingsSectionView.xaml).
- **Invoice footer discount controls**: WPF adds a linked **Final** amount input next to the prototype's Discount input. Operators can type either the discount amount or desired final total, and the other value updates immediately.
- **Bills selection footer**: the prototype shows `Clear · Retry All · Repost… · Print`. WPF adds a `Delete Selected` button that is admin-gated (hidden when the admin session is locked). When admin is locked — the typical operator state — the visible footer matches the design exactly. When unlocked, the extra button is a power-user affordance documented in [CLAUDE.md](../CLAUDE.md) under "Desktop admin-gating pattern".
- **`Posted · edit` chip**: WPF renders this as `Posted · edit` (matching the prototype) when `EditedAfterPush == true`. Source: `BillListRowViewModel.StateChipLabel`.

## 10. Acceptance criterion

For every screen, the acceptance test is a side-by-side comparison against the corresponding file in [design/](design/): the painted mockup's behavior must match what ships.
