# UI Design Reference

## 1. Purpose

The visual and interaction target for the Tally Wrapper desktop UI is the **Ledger Clear** redesign: 28 pictures in [docs/design/redesign/](design/redesign/), one per screen, state or dialog, plus two boards of the tokens and components. The older HTML/CSS/React prototype in [docs/design/](design/) is kept for its sample data and its keyboard rules; where it disagrees with the pictures, the pictures win.

This document is the contract between the pictures and the WPF code: what the UI should look and feel like, and which resource or control carries each part. The tokens are in [Resources/DesignTokens.xaml](../src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml), the button, text and input styles in [Resources/Styles.xaml](../src/ShowroomBilling.Desktop/Resources/Styles.xaml), and the templated controls in [Resources/Controls.xaml](../src/ShowroomBilling.Desktop/Resources/Controls.xaml).

## 2. The idea

- **Readable before pretty.** Sentence-case labels at 12.5 px (never upper case), 13.5 px body text, tabular figures so decimals line up, and a text colour for every size that passes 4.5:1 (`InkSoft` is the lightest text that may carry meaning).
- **Indigo is for action and focus only**: the main button, the focus edge, the active tab, the selected row. Status is a soft fill, dark ink **and** a word or a shape, never colour alone.
- **Result columns are tinted.** In the estimate table a column the operator cannot type in (net weight, rate, line total) sits on `ResultTint`, so the typing path reads as one white strip.
- **One focus rule on every control**: a 2 px indigo edge and a 3 px halo at 20 % (`AccentRing`). Buttons draw a white gap and a 2 px ring.

## 3. Tokens

The picture of every key is [Foundations-Tokens.png](design/redesign/Foundations-Tokens.png); [Foundations-Components.png](design/redesign/Foundations-Components.png) shows the controls.

| Group | Keys |
|---|---|
| Accent | `Accent` `#4F46E5`, `AccentHover`, `AccentPressed`, `AccentInk` `#3730A3` (text on soft indigo, 8.9:1), `AccentSoft`, `AccentSoftPressed`, `AccentSoftEdge`, `AccentRing` |
| Surfaces | `Bg` `#F4F5F8`, `BgPanel` white, `BgSunken` (column heading band, dialog foot), `ResultTint`, `ResultTintActive`, `BgHover`, `BgSelected`, `FocusStrip` |
| Lines | `BorderBrushTok`, `BorderStrong`, `BorderInput` (the edge of an input, 3:1), `BorderInputHover`, `Divider` |
| Text | `Ink`, `InkMuted`, `InkSoft`, `InkDisabled` (a disabled control, a dash or a placeholder mark only), `InkInverse` |
| Status | `Ok`, `Warn`, `Err`, `Info`, each with `…Soft`, `…Ink`, `…Border`; `ErrText` for error text and a field that needs attention; `NeutralChip` |
| Buttons off | `BtnDisabledInk`, `BtnPrimaryDisabledInk` |
| Dialogs | `DialogScrim`, `DialogShadow`, `PopupShadow` |

Do not write a hex colour in a view. If a colour is missing from the sheet, add it there.

### 3.1 Density (user-switchable, Standard by default)

| Key | Compact | Standard | Comfortable |
|---|---|---|---|
| `RowHeight` (table row) | 32 | 38 | 44 |
| `ListRowHeight` (Bills list row) | 36 | 42 | 48 |
| `InputHeight` / `ButtonHeight` | 30 | 34 | 40 |
| `FontSizeUi` | 12.5 | 13.5 | 14.5 |
| `FontSizeLabel` / `FontSizeSm` / `FontSizeSectionTitle` | 11.5 | 12.5 | 13.5 |
| `FontSizeXs` | 10.5 | 11 | 12 |
| `FontSizeMono` | 12 | 12.5 | 13.5 |
| `CellPaddingX` (`CellPadding` follows) | 8 | 10 | 12 |

Settings → Advanced → Display density switches them. `UiDensityManager` overrides these keys in the application resources, so every view and style must read them with `DynamicResource`. `UiDensityTests` checks that the manager's Standard column equals `DesignTokens.xaml`. A preference file written by the old two-level setting is migrated (version 2): the old "Compact" becomes Standard.

Sizes that do not change with density: `FontSizeTotal` 32, `FontSizeTitle` 22, `FontSizeDialogTitle` 17, `FontSizeHeading` 16, `FontSizeLineTotal` 14. Radii: 4 (inputs, chips), 6 (buttons), 8 (cards, menus), 10 (dialogs).

### 3.2 Typography

- `UiFontFamily`: Segoe UI Variable Text, then Segoe UI Variable, then Segoe UI. Tabular figures are set where a style needs them (the base `TextBlock` and `TextBox` styles do).
- `MonoFontFamily`: Cascadia Mono, then Consolas. Only for key chips and for numbers that are identifiers a person may read out or copy (a path, a timestamp field). An invoice number in a dialog is set in the UI font.
- Icons: `IconFontFamily`, Segoe Fluent Icons then Segoe MDL2 Assets. Glyphs are written as `&#xE73E;` in XAML.
- Indian grouping (`2,89,706`) comes from `UiCulture` (en-IN), which `App` applies to every element; code that formats a number uses `UiCulture.Rupees`, `Amount` and `Weight`.

### 3.3 DPI and monitor work areas

- The WPF desktop and the WinForms server dashboard run as Per-Monitor V2 DPI-aware processes. Do not replace this with system-DPI or bitmap scaling.
- The custom chrome handles `WM_GETMINMAXINFO` and derives maximize bounds from the current monitor's work area (`CaptionHeight` is 44, the height of the top bar).
- UI smoke tests should cover 100 %, 125 % and 150 % scaling where available, plus maximizing and moving between monitors with different scale factors.

## 4. Components

| Piece | Where | Notes |
|---|---|---|
| Buttons: `BtnBase` (secondary), `BtnPrimary`, `BtnDanger` (outlined red), `BtnDangerFilled` (the red button of a destructive confirm), `BtnGhost`, `BtnAccentOutline`, `BtnSm` 30, `BtnLg` 38, `BtnPrimaryLg` 40, `BtnIcon` | `Styles.xaml` | One template (`BtnBase`); the others set brushes only, and hover/pressed/disabled are style triggers. Never set `Background`, `BorderBrush` or `Foreground` on a `<Button>` in a view. |
| Key chip: `shell:Kbd.Hint="Ctrl+R"` on a button, `Kbd` / `KbdLg` / `KbdOnPrimary` elsewhere | `Styles.xaml`, `Shell/Kbd.cs` | The button template draws the chip. A button with no shortcut gets none. |
| Text boxes and password boxes | `Styles.xaml` | `shell:FieldAdorn.Placeholder`, `Prefix` and `Suffix` draw a hint, a mark such as ₹, and a unit inside the box. A multi-line box sets `VerticalContentAlignment="Stretch"`. |
| Combo box, table-cell combo, date picker, check box, radio button, context menu, scroll bars, progress bar, tooltip, `ChipToggle` | `Controls.xaml` | `shell:ComboExtras.DropDownMinWidth`, `DropDownFooter` and `ShowChevron`; a combo box also takes `shell:FieldAdorn.Placeholder`. The progress bar fills and restarts while `IsIndeterminate`. |
| Chips: `Chip`, `ToneChip` + `ToneChipText` (tone in `Tag`: ok, warn, err, info, accent), `shell:StateChip` (bill state with its shape) | `Styles.xaml`, `Controls.xaml` | 24 px, radius 4, sentence case. The view model works the tone out; no state names in XAML. |
| Panels and banners: `Panel`, `DialogPanel`, `InlineBanner` and `AdminBannerOk`/`Info`/`Warn`/`Err`, full-width `BannerInfo` | `Styles.xaml` | |
| `shell:DialogFrame` | `Controls.xaml`, `Shell/DialogFrame.cs` | Every dialog. See §6. |
| `shell:ReadingColumn`, `shell:FillCell` | `Shell/` | Layout. A settings section is a `ReadingColumn` (as wide as the page allows up to `MaxContentWidth`, at the left). A cell of a star-width table column is a `FillCell`: it fills its column and asks for no width, so one long name cannot push a row's columns away from the heading. |
| `shell:TypeAhead` | `Shell/TypeAhead.cs`, `TableCellCombo` in `Controls.xaml` | Suggestions while typing in an editable combo box (`IsEnabled`, `Path`, optional `Footer` and `MinWidth`); turn the combo box's own `IsTextSearchEnabled` off. The list is the template's `PART_Suggest` popup, apart from the drop-down. Its Up/Down/Esc handling is a class handler, because a combo box steps through its entries in its own key handling, which runs before any handler added to one instance. |
| `shell:RichText.Markup` | `Shell/RichText.cs` | A sentence with `**bold**` words in a `TextBlock`, from one bound string. |
| `shell:FocusOnShown`, `PasswordBinding`, `LazyViewHost` | `Shell/` | Unchanged. |

## 5. Screens

Each screen has a picture; compare against it, not against the prototype.

| Screen | Picture |
|---|---|
| Shell: top bar (brand, three tabs with their keys, Cloud / Database / Tally health, operator, window buttons), banner, bottom bar (the shortcuts of the open screen, company, fiscal year, workstation, time) | [Shell.png](design/redesign/Shell.png) |
| Invoice: header, line-item table, focus strip under it, totals, actions bar | [Main.png](design/redesign/Main.png), [Estimate-New.png](design/redesign/Estimate-New.png), [Estimate-Search.png](design/redesign/Estimate-Search.png), [Estimate-Table.png](design/redesign/Estimate-Table.png) |
| Bills | [Bills.png](design/redesign/Bills.png), [Bills-Actions.png](design/redesign/Bills-Actions.png) |
| Limited mode | [Limited-Mode.png](design/redesign/Limited-Mode.png) |
| Settings: Connection, Invoice, Print layout, Ledgers, Masters, Server, Hand-over, Advanced, Admin | `Settings-*.png` (Hand-over has no picture here; see the next row) |
| Dialogs: Confirm, Reason, Change number, Admin unlock, Shortcuts, System health, Bill details, Print preview, First-launch setup, Batch data scheduler | `Dialog-*.png` |
| Bill hand-over: the toast, the result dialog in each case, the admin unlock for a bill in Tally, the key dialog, Settings → Hand-over | no picture in this folder; `HandoverViewSnapshotTests` saves each as `Handover-*.png`. What they say: [18 §6, §8](18_bill_handover.md) |

### 5.1 Behaviour the pictures add to the prototype

- **Estimate table.** The row that holds keyboard focus is `ResultTintActive` with an indigo zone line; the strip under the table says how that line was priced (`LineExplainer`) and which rate it used (`RateLadder`). A gold line with no karat says so in its karat cell.
- **Totals.** Subtotal before GST, CGST, SGST, discount (with its percentage of the total), round off and the grand total at 32 px; discount and final amount are linked inputs.
- **New bill.** The button beside Print estimate reads **Clear** while the form holds a bill not saved yet and **New bill** while it holds a saved one (opened with Edit from Bills); either way it empties the form for a new bill and leaves any saved bill as it is. Its key is `Ctrl+Shift+N`, not the picture's `Esc Esc` (`Esc` already closes dialogs and suggestion lists, and `Ctrl+N` is New row). It asks first, with Cancel focused, when the form has changes not saved.
- **Bills.** Counts of each state across the top, a chip per row (shape plus word), a note per row, and a footer that names what the selection can do. Admin-only actions stay hidden while locked (see [CLAUDE.md](../CLAUDE.md)).
- **Settings.** Each section says whether it is shared by every counter or this PC only. A section is read-only until **Edit**; then a band offers **Discard** and **Save all**. A ledger field says whether Tally has that ledger.
- **First-launch setup.** A rail of three steps (done, current, to come), three sections with a chip each, and a foot that says what is missing. **Finish setup** stays off until all three are done.

## 6. Dialogs

A dialog is a `UserControl` overlay hosted once in `MainWindow.xaml`; it is never a `<Window>`. Its only child is a `shell:DialogFrame`:

```xml
<shell:DialogFrame Title="Change invoice number" Subtitle="{Binding ContextText}"
                   HeaderChips="{StaticResource AdminOnlyChip}" PanelWidth="520"
                   DataContext="{Binding ChangeNumberDialog}" CloseCommand="{Binding CancelCommand}">
    <shell:DialogFrame.Footer> … buttons … </shell:DialogFrame.Footer>
    … body …
</shell:DialogFrame>
```

- **Anatomy.** The frame paints the scrim and the shadow; the head has an optional icon tile (`HeaderGlyph`), a 17 px title, chips, a 12.5 px context line and a close button; the body scrolls when the window is short; the foot is a grey band with `FooterNote` on the left and `Footer` (the buttons) on the right. A dialog sets none of these itself.
- **Keys.** `Esc` cancels and `Enter` confirms; the buttons carry `shell:Kbd.Hint="Esc"` / `"Enter"`. A warning question focuses Cancel.
- **Sizes.** `PanelWidth` (and, for Print preview, `PanelHeight` with `PanelMinWidth`/`PanelMinHeight`, and `Overlay` for the resize grip). The panel never grows past the window less 24 px a side.
- **`DataContext` is set on the frame**, so a command for the shell (`CloseDialogCommand`) is reached with `RelativeSource AncestorType=UserControl`, which reads the dialog's own `DataContext`, the shell.
- **A tone for the icon tile.** `HeaderTone="warn"` draws the head's tile amber, for a dialog that reports something gone wrong (the hand-over result); the default is the soft indigo tile.
- **The one window that is not a dialog** is the hand-over's toast (`Shell/ToastWindow`): a card at the bottom right of the screen that must show while the main window is behind another program, so it is a top-most window that never takes the focus (`WS_EX_NOACTIVATE`) and fades by itself. Its content is a `UserControl` (`HandoverToastView`) like any view. Anything the operator must answer stays an overlay in the main window, brought to the front.
- **Yes/no questions** go through `ConfirmDialogViewModel`: a `ConfirmRequest` has a title (the question), a message, an optional `Note` (the line to read twice, in a banner) and flags `IsWarning` and `IsDestructive` (a red confirm button and a trash icon). Questions two screens share are worded once in `BillConfirmations`.
- **Reason and change-number prompts** take a `ReasonRequest` (title, context line, warning with `**bold**`, confirm text) and the change-number configuration, and complete a `TaskCompletionSource` held by the shell.
- **Shortcuts** are listed from `ShortcutCatalog`; change it with the key bindings.

## 7. Keyboard-first rules

Global (registered in the app shell):

- `Ctrl+1` Invoice tab · `Ctrl+2` Bills tab · `Ctrl+3` Settings tab
- `?` open the shortcuts list (not while typing in a text box)
- `` ` `` open the admin unlock
- `Esc` close the top-most dialog
- `F9` print estimate (Invoice screen active)
- `Ctrl+Shift+N` new bill (Invoice screen active; in the shortcuts list and on its button, not in the bottom bar)
- `Ctrl+S` saves the active screen: Save Bill on Invoice, Save in Settings.

The bottom bar lists the keys of the open screen:

- Invoice: `Tab Next field · Shift+Enter Back · F3 Quick add · Ctrl+N New row · Ctrl+Del Remove row · F9 Print estimate · Ctrl+S Save`
- Bills: `Enter Open details · Ctrl+R Retry push · Ctrl+Shift+R Repost · Ctrl+Shift+E Edit · Ctrl+P Print · PgUp / PgDn Page` (`F5` refreshes and is in the shortcuts list)
- Settings: `Ctrl+S Save · Esc Cancel · Tab Next field · ? Shortcuts`

Invoice line-entry tab order:

- Row cell order: `name → karat → gross wt → less wt → net wt → unit → wastage → labour → diamond rate → extra` (the karat sits next to the item, so what is sold is named before it is weighed; a diamond line has no karat, and its flat rate takes the place of wastage and labour). All three weight cells are editable for gold lines: changing Gross or Less recalculates Net as `max(0, Gross Wt − Less Wt)`; changing Net recalculates Less as `max(0, Gross Wt − Net Wt)`.
- `Tab` advances one cell; at the last cell of a row it wraps to cell 0 of the next row (creating a new row if needed). `Shift+Tab` reverses.
- `Enter` advances one cell (same order as `Tab`); `Shift+Enter` reverses.
- Suggestions in the item and karat cells (`shell:TypeAhead`): as soon as text is typed a small list opens under the cell with the entries that contain every word typed, narrowing with each letter. `↑↓` move through that list only while it is open (they select nothing and never change the text), `Enter` picks the highlighted entry and goes on to the next cell, `Tab` keeps what was typed (a free-text item is fine), `Esc` closes the list. With no list open, `↑↓` in any cell of the line table go to the same cell of the row above or below; they never step a combo box through its entries, so the item and the unit never change under the arrow keys.

Invoice end-to-end `Enter` flow (header → lines → save → print):

1. Cursor lands on **24kt Rate** when the Invoice screen becomes visible.
2. `Enter` on 24kt Rate → **Party** field.
3. `Enter` on Party → **Item** cell of the first row.
4. Inside a row, `Enter` advances cell by cell. `Enter` on the **Item** cell of an empty row jumps to **Save bill**.
5. `Enter` on Save bill saves; on success the Print Preview dialog opens with focus on its **Print** button.
6. `Enter` in Print preview prints. `Esc` closes the dialog.

## 8. System-state visuals

Two operating states, driven by whether the API is reachable. Tally itself is only touched synchronously when the operator clicks Push or Refresh, so there is no live Tally health: its dot stays neutral until an operator action says otherwise.

| State | Banner | Health cluster |
|---|---|---|
| healthy | none | green dot on Cloud and Database; neutral dot on Tally |
| limited | error banner | red dots |

In `limited`, Invoice and Bills are hidden and the main region shows the Limited-mode card, whose **Server settings** button opens Settings → Server. WPF enters `limited` in three cases, each with its own banner: the server does not answer, the server cannot reach its database, and the database has not had this build's migrations ("Database upgrade needed", with the database pill saying "Database behind").

The one banner under the top bar is `BannerView`: a note (information, can be dismissed) or a problem (cannot).

## 9. Seeing it without launching the app

- `ViewSmokeTests` loads every view against the real shell view model and fails on a XAML load error or a binding error.
- Set `TALLYWRAPPER_VIEW_SNAPSHOTS` to a folder and run the Desktop tests: `ViewSmokeTests` saves each view as a PNG, and `DesignSnapshotTests` and `DesignDialogTests` save each screen and dialog with the sample data of its mockup, named like the mockup (`Main.png` becomes `Design-Main.png`, `Dialog-Confirm.png` becomes `Design-Dialog-Confirm.png`). Put the two side by side.
- Hover, press and disabled states of the buttons are covered by `ButtonStyleTests`.
- The sample showroom is in `DesignSample` (company, ledgers, ten items, three karats, nine bills in every state).

## 10. Deliberate departures from the pictures

- **Tweaks panel** of the prototype: review-only. Density lives in Settings (workstation-local; see [14_settings_storage_contract.md](14_settings_storage_contract.md)); system state comes from real health signals; keyboard hints stay on; accent is fixed at indigo.
- **Window buttons** are drawn by the top bar, because the window uses `WindowStyle="None"` with `WindowChrome`.
- **Settings → Server** replaces the prototype's cloud-onboarding form: the server address, the address in use, **Test server**, **Find server** and **Save and restart Billing**. The database connection is set from the tray on the server PC.
- **Bill details** keeps **Revise…** (the mockup omits it) because the action exists for pending bills.
- **Bills "last error" note** is generic text for a failed row: the list item carries no error text, and adding it is a server change.
- **Picker popup** shows names only: the item master has no karat to filter or price by.
- **Column order.** The estimate pictures show Karat after Unit; it is shipped next to the item name, at the owner's request.
- **Empty estimate.** The pictures show a "Start a bill from the keyboard" card in the middle of an empty table; it is not shipped. The empty table is left bare.
- **Dates** follow the machine's en-IN short format in the Invoice header, not "11 Oct 2026".

## 11. Acceptance criterion

For every screen, the acceptance test is a side-by-side comparison against its picture in [design/redesign/](design/redesign/), then a click-through on a scratch run (`tools\run-scratch.ps1`) for focus and keyboard flow, which the snapshots cannot show.

## 12. The server dashboard (WinForms)

`TallyWrapper.Server.exe` has no picture in [design/redesign/](design/redesign/). Its window, its dialogs and its tray menu are drawn in the same language, in WinForms, in `src/ShowroomBilling.ServerTray/Ui/`:

- **`Theme.cs` is a copy of the tokens this document lists** (colours, the three radii, the type sizes). WinForms cannot read `DesignTokens.xaml`: a value changed there is changed in `Theme.cs` too.
- **`Controls.cs` holds the parts**, each drawn by hand to match its Desktop counterpart: `Card`, `FlatButton` (secondary, primary, danger outline, danger filled, text only; hover, pressed, focus and disabled; an optional key chip such as `Esc`), `Chip`, `StatusDot`, `FieldBox`, `CounterTable`, `NoteBanner`, `GlyphTile`, `FootBand` and `TextLabel`. Do not put a stock `Button`, `Label` or bordered `TextBox` in the window: they bring the system's look and their own padding.
- **`TrayDialog` is the one dialog**: a glyph tile, the title, a sentence, what the case needs, and the grey foot with the action last. It tells (`ShowMessage`), asks (`Confirm`) or takes one line (`Prompt`). There is no `MessageBox.Show` in the project.
- **What the window says is decided in `DashboardState`**, not in the form: the sentence at the top, the three health lines, their tones and which service button is offered. `DashboardStateTests` holds the wording.
- **Sizes are written for 100 % scaling**, and the form declares `AutoScaleDimensions` last, after everything is in it (see the comment in `StatusForm`'s constructor). Declared first, WinForms scales an empty window and leaves the contents unscaled, which at 125 % cuts the page off. Hand-drawn controls scale by `DeviceDpi` (`Theme.Px`, `Theme.Font(role, DeviceDpi)`).
- **To look at it without installing a server**, set `TALLYWRAPPER_VIEW_SNAPSHOTS` and run `ServerDashboardViewTests`: it saves the dashboard in six states, six dialogs and the tray menu as `Server-*.png`, without showing a window. Add `TALLYWRAPPER_SNAPSHOT_SCALED=1` (and run that class alone) to draw them at the scaling of the screen in front of you. A one-line text box of a window that is not on screen draws nothing, so the test writes its text into the picture; hover and focus are not in the pictures.
