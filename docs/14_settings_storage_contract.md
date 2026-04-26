# Settings Storage Contract

This note is the implementation-level reminder for the V2 foundation scaffold.

## Local storage is allowed only for

- bootstrap/startup config needed to launch the desktop (API URL, counter name)
- protected secrets/tokens (admin unlock token cache, TTL-bounded)
- logs
- truly workstation-local UX preferences such as printer choice or last PDF directory

## Cloud/backend owns the source of truth for

- Tally connection settings (host, port, active company, timeout)
- numbering settings (prefix, suffix)
- print settings (company info, bank details, T&C, copy defaults)
- ledger mappings (sales, cash, credit/debit, CGST, SGST, round-off, discount)
- voucher type settings
- item master data
- karat/stock mappings
- admin/shared operational settings
- any setting that changes shared business behavior across counters

## Enforcement in this foundation

- desktop local config is limited to `DesktopBootstrap` and `DesktopLocalPreferences`
- **there is no separate bridge process** — its config has been removed. Tally connection details (host, port, active company) live in cloud settings and are read by the API's `ITallyPoster` on each posting call.
- API exposes settings ownership through `/api/settings` and `/api/settings/storage-contract`
- shared settings are represented as API-owned, not as local files or a local settings database
- tests verify that desktop appsettings files do not introduce shared/business settings sections (see `ConfigFilesTests`)

## Explicit non-goal

No local durable business state is introduced for bills, numbering, or master snapshots. (There is also no queue state to store — posting is synchronous and inline in the API, so there is no queue anywhere.)
