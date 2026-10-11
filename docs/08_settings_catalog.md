# Settings

This page says where each kind of setting is defined, validated and edited. It does not list the fields: the code below is the list.

## Shared settings (stored by the API, one `cloud_settings` row)

- **Fields:** `EffectiveCloudSettingsDto` in [`Contracts/Settings/EffectiveSettingsResponse.cs`](../src/ShowroomBilling.Contracts/Settings/EffectiveSettingsResponse.cs). It has five groups: `Connection` (Tally host, port, timeout, active company), `Numbering` (prefix, suffix, padding), `Print` (company and bank details, terms, copy defaults, font sizes), `Ledgers` (ledger mappings and the sales voucher type) and `Masters` (bill items and karats, as JSON; rows are `ItemMasterEntry` and `KaratMasterEntry`).
- **Print layout:** `PrintLayoutSettings` in [`Contracts/Settings/PrintLayoutSettings.cs`](../src/ShowroomBilling.Contracts/Settings/PrintLayoutSettings.cs): margins, logo / signature / watermark placement, page density, border and the ordered invoice sections. The images are rows in `print_assets`.
- **Validation:** `CloudSettingsValidator` in [`Application/Settings/CloudSettingsValidator.cs`](../src/ShowroomBilling.Application/Settings/CloudSettingsValidator.cs) runs on every save. Its ranges come from `SettingLimits` and `PrintLayoutDefaults` in Contracts, which the Desktop reads too, so a value the Desktop accepts is one the server stores. A refused value is a `400` whose `detail` names the field.
- **Endpoints:** `GET` / `PUT /api/settings`, `POST /api/settings/company/select`, `GET` / `PUT /api/settings/print-layout`, `/api/print-assets`. See [09_api_spec.md](09_api_spec.md) §2.5 and §2.6.

## Where the Desktop edits them

Settings tab, left-hand sections (`SettingsSectionKey`; views under `src/ShowroomBilling.Desktop/Views/Settings/`):

| Section | Edits |
|---|---|
| Server | The address of the Tally Wrapper server this workstation works against. Local to the PC, not a shared setting; saving it restarts Billing. |
| Connection | Tally host, port, timeout; the active company. |
| Invoice | Company and bank details, invoice prefix / suffix / padding, copy defaults, font sizes, terms and conditions. |
| Print Layout | Margins, density, border, section order and visibility, logo / signature / watermark. Saved by its own **Save** button. |
| Ledgers | Ledger mappings and the sales voucher type. |
| Masters | Bill items and karats. |
| Advanced | UI density (local to the PC) and the log / app-data / install folders. |
| Admin | Shown only while admin is unlocked: change the passcode, open the Batch Data Scheduler. |

Connection, Invoice, Ledgers and Masters are edited as one draft (`SettingsDraft`) and saved together by **Save All**. Lists that come from Tally (companies, ledgers, voucher types, stock items) are refreshed by the **Refresh from Tally** button of the section that uses them.

## Not shared settings

- The admin passcode is set through `/api/admin/passcode` (the unlock dialog the first time, Settings → Admin afterwards). It is stored hashed in `admin_passcodes`, not in `cloud_settings`.
- The server's database connection is set only from the tray on the server PC. No Desktop screen shows or changes it.
- Workstation preferences (printer, PDF folder, preview zoom, UI density) and the server address stay on the PC. What may be stored locally, and where, is in [14_settings_storage_contract.md](14_settings_storage_contract.md).
