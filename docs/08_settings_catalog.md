# Settings Catalog

This document catalogs all settings and related actions that matter for Tally Wrapper.

Scope rules:

- prefer migration inventory over older requirement docs
- preserve current behavior where marked migration-critical
- state redesigns explicitly

---

## 1. Field catalog

| Field / control | Page / section | Description | Validation | Persistence scope | Immediate side effects | Related save/test/fetch actions |
|---|---|---|---|---|---|---|
| `host` | Connection / Server Configuration | Tally host or IP address | Required for company fetch and full save | Shared runtime setting | Used by reachability probe and Tally fetches | `Fetch Companies`, `Save All` |
| `port` | Connection / Server Configuration | Tally port | Numeric, valid port range | Shared runtime setting | Used by the API's `ITallyXmlClient` on every push / master refresh | `Fetch Companies`, `Save All` |
| `timeout_seconds` | Connection / Server Configuration | Tally response timeout | Range-bound integer | Shared runtime setting | Changes Tally call timeout behavior | `Save All` |
| `counter_name` | Connection / Server Configuration | Local counter display name | Optional | Local-only workstation/device setting | Affects local UI identity | `Save All` |
| `company_name` | Connection / Active Tally Company | Selected Tally company name | Required for full save | Shared runtime setting | Changing it immediately refreshes company-dependent state | `Fetch Companies`, company selection, `Save All` |
| `print_company_name` | Invoice / Company Details on Invoice | Printed company name | Required | Shared print setting | Immediately affects preview content after save | `Save All` |
| `company_gstin` | Invoice / Company Details on Invoice | Printed GST number | Optional | Shared print setting | Affects invoice header/body | `Save All` |
| `company_phone` | Invoice / Company Details on Invoice | Printed phone number | Optional | Shared print setting | Affects invoice header/body | `Save All` |
| `company_address` | Invoice / Company Details on Invoice | Printed address | Optional | Shared print setting | Affects invoice header/body | `Save All` |
| `company_state` | Invoice / Company Details on Invoice | Printed state | Optional | Shared print setting | Affects place/state rendering | `Save All` |
| `company_country` | Invoice / Company Details on Invoice | Printed country | Optional | Shared print setting | Affects place/country rendering | `Save All` |
| `bank_name` | Invoice / Bank Details | Printed bank name | Optional | Shared print setting | Affects bank details section | `Save All` |
| `bank_account` | Invoice / Bank Details | Printed account number | Optional | Shared print setting | Affects bank details section | `Save All` |
| `bank_ifsc` | Invoice / Bank Details | Printed IFSC | Optional | Shared print setting | Affects bank details section | `Save All` |
| `bank_upi` | Invoice / Bank Details | Printed UPI ID | Optional | Shared print setting | Affects bank details section | `Save All` |
| `terms_and_conditions` | Invoice / Terms & Conditions | Printed terms text | Optional | Shared print setting | Affects footer/terms block | `Save All` |
| `invoice_prefix` | Invoice / Numbering | Prefix for visible invoice number | Optional | Shared numbering/print setting | Triggers next-number preview refresh | live preview, `Save All` |
| `invoice_suffix` | Invoice / Numbering | Suffix for visible invoice number | Optional | Shared numbering/print setting | Triggers next-number preview refresh | live preview, `Save All` |
| `copy_original_default` | Invoice / Print Copies | Default original copy checkbox | Boolean | Shared print setting | Seeds preview copy defaults | `Save All` |
| `copy_duplicate_default` | Invoice / Print Copies | Default duplicate copy checkbox | Boolean | Shared print setting | Seeds preview copy defaults | `Save All` |
| `copy_triplicate_default` | Invoice / Print Copies | Default triplicate copy checkbox | Boolean | Shared print setting | Seeds preview copy defaults | `Save All` |
| `logo_path` | Invoice / Layout Assets | Optional logo source path | Optional file path | Local file selection + shared print asset reference/data | Affects rendered output | Browse, Clear, `Save All` |
| `signature_path` | Invoice / Layout Assets | Optional signature source path | Optional file path | Local file selection + shared print asset reference/data | Affects rendered output | Browse, Clear, `Save All` |
| `print_font_size` | Invoice / Layout Editor | Base print font size | Positive integer | Shared print layout setting | Affects rendered output | Layout Editor, `Save All` |
| `print_terms_font_size` | Invoice / Layout Editor | Terms font size | Positive integer | Shared print layout setting | Affects rendered output | Layout Editor, `Save All` |
| `margin_left_mm` | Invoice / Layout Editor | Left margin | Numeric | Shared print layout setting | Affects page composition | Layout Editor, `Save All` |
| `margin_top_mm` | Invoice / Layout Editor | Top margin | Numeric | Shared print layout setting | Affects page composition | Layout Editor, `Save All` |
| `margin_right_mm` | Invoice / Layout Editor | Right margin | Numeric | Shared print layout setting | Affects page composition | Layout Editor, `Save All` |
| `margin_bottom_mm` | Invoice / Layout Editor | Bottom margin | Numeric | Shared print layout setting | Affects page composition | Layout Editor, `Save All` |
| `logo_width`, `logo_height`, `logo_left`, `logo_top` | Invoice / Layout Editor | Logo geometry | Numeric | Shared print layout setting | Affects output placement | Layout Editor, `Save All` |
| `signature_width`, `signature_height`, `signature_left`, `signature_top` | Invoice / Layout Editor | Signature geometry | Numeric | Shared print layout setting | Affects output placement | Layout Editor, `Save All` |
| `sales_ledger` | Ledgers / Sales | Primary sales ledger mapping | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `cash_ledger` | Ledgers / Payment | Cash payment ledger | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `credit_debit_ledger` | Ledgers / Payment | Card/UPI/non-cash ledger | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `cgst_ledger` | Ledgers / Tax | CGST ledger | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `sgst_ledger` | Ledgers / Tax | SGST ledger | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `round_off_ledger` | Ledgers / Adjustments | Round-off ledger | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `discount_ledger` | Ledgers / Adjustments | Discount ledger | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Ledgers`, `Save All` |
| `sales_voucher_type` | Ledgers / Voucher Type | Voucher type for Tally posting | Required | Shared Tally mapping | Affects posting XML builder inputs | `Fetch Voucher Types`, `Save All` |
| `item_master_rows` | Masters / Bill Items | Software-side catalog of bill items. Per row: `name`, `unit` (`gm`/`ct`), `item_category` (`gold_based`/`diamond`), `pricing_mode` (`wastage`/`labour`/`both` — meaningful only for gold_based), `wastage_percent`, `default_labour_per_gram`, `default_stock_mapping_label` (used as the Tally `STOCKITEMNAME` fallback when the line has no karat mapping). Items are operator-defined; they are NOT Tally entities — karats bridge them to Tally stock items. JSON blob lives in `MasterDataSettingsDto.ItemMasterDataJson` (round-tripped 1:1 with V1 snake_case). | Name required; unit ∈ {gm, ct}; `item_category` ∈ {gold_based, diamond}; `pricing_mode` ∈ {wastage, labour, both}; numerics ≥ 0 | Shared master data | Refreshes sales item lookup in the Invoice tab; `default_stock_mapping_label` feeds the post payload's `StockName` fallback | `Add Item`, `Remove`, `Save All` |
| `karat_mapping_rows` | Masters / Karats | Karat definitions mapped to Tally stock items. Per row: `label` (e.g. "18K"/"22K"/"24K"), `purity_percent` (e.g. 75 / 91.6 / 99.9), `tally_item` (the Tally stock-item name this karat posts into). Purity drives the per-line effective rate via `rate_24kt × purity% / 100`; `tally_item` is the payload's stock name at Post time. JSON blob lives in `MasterDataSettingsDto.KaratMappingDataJson`. | Label + `tally_item` required; `purity_percent` 0–100; `tally_item` should match a name returned by `Fetch Stock Items` (free-text tolerated if cache empty) | Shared master data | Refreshes sales karat lookup and drives posting stock-item resolution | `Add Karat`, `Remove`, `Fetch Stock Items`, `Save All` |
| `admin_passcode` | Advanced / Admin Mode | Shared admin unlock passcode | Non-empty, confirmation must match | Admin-only | Unlock/admin semantics | `Save Admin Passcode` |

---

## 2. Actions catalog

| Action | Page / section | Purpose | Scope | Immediate effect |
|---|---|---|---|---|
| `Fetch Companies` | Connection | Load active companies from Tally | Operator | Updates company list and connection state |
| Company selection | Connection | Set active company immediately | Operator | Rebuilds company-dependent runtime and refreshes masters |
| `Test Connection` (DB backend) | Connection / Database Backend | Verify remote database credentials | Admin-only in remote mode | Updates test status label only |
| `Save Database Settings` | Connection / Database Backend | Switch backend configuration | Admin-only in remote mode | Reloads runtime/backend on success |
| `Browse` / `Clear` logo | Invoice / Layout Assets | Manage print logo | Operator/admin | Changes local form state before save |
| `Browse` / `Clear` signature | Invoice / Layout Assets | Manage print signature | Operator/admin | Changes local form state before save |
| `Open Print Layout Editor` | Invoice / Layout Assets | Open calibration editor | Operator/admin | Allows preview-driven layout adjustment |
| `Refresh Ledgers` | Ledgers | Fetch ledger names from Tally | Operator | Updates combo sources |
| `Fetch Voucher Types` | Ledgers | Fetch voucher types from Tally | Operator | Updates voucher type combo |
| `Refresh Stock Items` | Item Master | Fetch stock items from Tally | Operator | Updates stock item source list |
| `Refresh Stock Items` | Karat Mapping | Fetch stock items from Tally | Operator | Updates stock item source list |
| `Refresh Masters` | Advanced | Refresh masters/status/logically dependent data | Operator | Updates status dot, timestamp, master state |
| `Open Current Log` | Advanced | Open current log file | Operator/admin | Diagnostic action only |
| `Open Logs Folder` | Advanced | Open log folder | Operator/admin | Diagnostic action only |
| `Save Admin Passcode` | Advanced / Admin Mode | Create/update admin passcode | Admin-only | Updates admin security state |
| `Refresh Recovery List` | Advanced / Lock Recovery | Load stale draft locks | Admin-only | Updates recovery list |
| `Recover Selected Lock` | Advanced / Lock Recovery | Release stale edit lock | Admin-only | Frees locked draft |
| `Save All` | Dialog shell | Persist combined settings payload | Mixed by field scope | Refreshes runtime and dependent data |

---

## 3. Persistence scopes

| Scope | Meaning |
|---|---|
| `Local-only` | Workstation/device preference or non-business local UX state |
| `Shared` | Must be stored centrally and applied consistently across counters/API |
| `Admin-only` | Shared but only editable after admin unlock/role confirmation |
| `Bootstrap-only` | Relevant only during initial setup/limited-mode/bootstrap flow |

### Tally Wrapper persistence rules

- Tally/business settings live in cloud backend/PostgreSQL.
- Printer name and PDF directory may remain local-only.
- Admin unlock is a UI/session behavior; admin-authorized changes are still server-owned.
- Database configuration is a local Postgres connection override stored outside shared settings.

---

## 4. Hidden admin-mode behaviors

| Behavior | Current system | Tally Wrapper treatment |
|---|---|---|
| `~` unlock shortcut | Hidden session unlock in settings | Preserve as migration-critical admin behavior unless replaced with explicit UI |
| Local SQLite no-unlock admin mode | Auto-grants admin features in local mode | Retire if local SQLite mode is removed |
| Lazy-load lock recovery on advanced page | Recovery list loads only after admin access | Preserve functionally |
| Synthetic-bill support shortcut | Hidden admin/debug flow | Out of scope for normal settings catalog |

---

## 5. Tally Wrapper redesign notes

### Preserve materially

- company fetch/use split — shipped (Connection pane, Fetch / Refresh / Set Active)
- preview vs save distinction for invoice numbering — shipped (Numbering uses preview-then-reserve)
- print asset/layout controls — shipped in Phase 10 slice 5 (`/api/print-assets` + `/api/settings/print-layout` with a dedicated Print Layout nav section)
- ledger/voucher/master fetch flows — shipped
- admin unlock and lock recovery concept — shipped in Phase 10 slice 5 (`~` keybinding opens `AdminUnlockDialog`, `/api/admin/*` + `/api/draft-leases/active` + force-release)

### Redesign intentionally

- backend switching: current system supports Local SQLite vs remote; Tally Wrapper target stack is PostgreSQL-backed cloud system, so this becomes deployment/bootstrap configuration rather than normal day-to-day operator switching
- delete-all-vouchers: current destructive local purge should not be copied blindly into cloud Tally Wrapper without explicit product sign-off — **still deferred as of Phase 10; not shipped**
