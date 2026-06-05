# Development Setup

## Prerequisites

- .NET SDK `10.0.202`
- Windows for the WPF desktop project
- PostgreSQL 17+ locally, or Docker Desktop if you want to use `docker-compose.dev.yml`

## Database

Primary development database is PostgreSQL 17+. Use a local Postgres instance, Docker, or a managed Postgres provider such as Neon. Real connection strings are intentionally not committed. Keep environment-specific values in ignored files (`src/ShowroomBilling.Api/appsettings.Development.json`, `src/ShowroomBilling.Api/appsettings.Production.json`), user-secrets, or local environment variables.

For Neon, use the **direct** endpoint host (`ep-<id>.<region>.aws.neon.tech`) rather than the `-pooler` host because EF Core migrations and warmup can hold long-lived connections. Set `Database:AutoMigrateOnStartup=true` only in private local environment files.

Manual migration command:

```powershell
dotnet ef database update --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --connection "<postgres-connection-string>"
```

Local fallback (only if you want an offline Postgres):

```powershell
docker compose -f docker-compose.dev.yml up -d
```

Then override `ConnectionStrings:Postgres` via user-secrets or a `.env` before launching. Default placeholder (ships in `appsettings.json`): `Host=localhost;Port=5432;Database=showroom_billing_v2;Username=postgres;Password=postgres`.

If the database is unreachable, API and desktop still start, but DB-backed settings and migrations remain unavailable until the database is reachable.

Each database also owns an identity marker in `public.database_identity`. After the `DatabaseIdentity` migration has run once, set the marker manually in each branch:

```sql
-- dev/test DB
insert into public.database_identity (key, value, updated_at_utc)
values ('environment', 'DEV', current_timestamp)
on conflict (key) do update
set value = excluded.value,
    updated_at_utc = excluded.updated_at_utc;

-- prod DB
insert into public.database_identity (key, value, updated_at_utc)
values ('environment', 'PROD', current_timestamp)
on conflict (key) do update
set value = excluded.value,
    updated_at_utc = excluded.updated_at_utc;
```

The desktop status bar displays this DB-owned marker (`DB DEV`, `DB PROD`, or `DB UNSET`). Runtime health treats a `Development` API connected to a non-`DEV` database, or a `Production` API connected to a non-`PROD` database, as a `DB MISMATCH` warning while still reporting PostgreSQL as reachable.

## Build

```powershell
dotnet restore ShowroomBilling.sln
dotnet build ShowroomBilling.sln
dotnet test ShowroomBilling.sln
```

## Run the API

```powershell
dotnet run --project src/ShowroomBilling.Api
```

Key endpoints:

- `http://localhost:5108/swagger`
- `http://localhost:5108/api/health/live`
- `http://localhost:5108/api/health/masters` (per-type snapshot freshness)
- `http://localhost:5108/api/runtime/bootstrap`
- `http://localhost:5108/api/masters/companies` (desktop read; also `ledgers`, `stock-items`, `voucher-types`)
- `http://localhost:5108/api/masters/refresh` (synchronously fetches from Tally and writes the snapshot — the only way masters get updated; there is no background polling)
- `http://localhost:5108/api/numbering/preview` (next visible invoice number; does not reserve)
- `http://localhost:5108/api/numbering/scopes` (per-scope `nextValue` plus cloud prefix/suffix)
- `http://localhost:5108/api/numbering/reserve` (POST idempotent reservation)
- `http://localhost:5108/api/bills` (bill search; POST `/drafts` to create; `PUT /drafts/{id}` to update; POST `{id}/push|revise|void`)
- `http://localhost:5108/api/bills/push-pending` (synchronously posts every pending bill to Tally, one at a time)
- `http://localhost:5108/api/bills/{id}/audit` (bill event trail, ordered by timestamp)
- `http://localhost:5108/api/bills/{id}/posting-status` (current state + last-post metadata)
- `http://localhost:5108/api/bills/{id}/retry` (re-run the sync push after a failure)
- `http://localhost:5108/api/bills/{id}/repost` (push again for a posted/failed bill)
- `http://localhost:5108/api/bills/synthetic-batch` (POST; admin-gated via `X-Admin-Token`; generates N `pending` bills with backfilled timestamps — see [docs/17_synthetic_batch.md](docs/17_synthetic_batch.md))

## Server API + tray mode

The standalone LAN deployment is a single server EXE. It embeds the API, installs/repairs the Windows Service, registers the tray for login, and keeps the tray companion running in the logged-in server session:

```powershell
.\tools\publish-server-tray.ps1
```

Copy `publish\server\ShowroomBilling.Server.exe` to the Tally server and run it. On first run it prompts for the trusted LAN CIDR, extracts `ShowroomBilling.Api.exe` to `C:\ProgramData\ShowroomBilling\bin`, installs the `ShowroomBilling.Api` service as automatic startup, sets `SHOWROOM_BILLING_APPDATA=C:\ProgramData\ShowroomBilling`, generates `C:\ProgramData\ShowroomBilling\maintenance_token.txt`, and creates a firewall rule scoped to that LAN CIDR. Running it again is idempotent: it repairs missing service/env/firewall/startup pieces and starts the service if needed.

Configure workstations from **Billing → Settings → Database → API Connection Mode**. Choose `Server`, enter `http://<tally-server>:5107`, and click **Save and restart**. The UI writes the typed local bootstrap override at `%APPDATA%\ShowroomBilling\desktop-bootstrap.local.json`:

```json
{
  "connectionMode": "Server",
  "serverApiBaseUrl": "http://tally-server:5107"
}
```

`LocalEmbedded` remains the fallback mode, but it is per-PC: that workstation must have its own local DB override and Tally settings must point at the Tally server by LAN name/IP rather than `127.0.0.1`.

The same Database settings section also exposes `LocalEmbedded` as the old connection method. It remembers the last non-localhost server URL, has **Test server** and **Find server** actions, and shows the local embedded API URL (`http://localhost:5107`). When the desktop is currently running in `Server` mode, the local embedded DB override editor is read-only because server DB setup is maintained from the server tray; the section still loads this workstation's saved LocalEmbedded fallback DB override so the operator can see what will be used after switching back.

## Run the desktop shell

```powershell
$env:DOTNET_ENVIRONMENT='Development'
dotnet run --project src/ShowroomBilling.Desktop
```

**Device-token auth on write paths.** Every mutating API endpoint (bill create/update/push/retry/repost/void/revise, settings PUT, masters refresh, print-asset upload/delete, draft-lease acquire/renew/release, numbering reserve) requires the `X-Device-Token` header. The token is a 32-byte random secret stored at `%LOCALAPPDATA%\ShowroomBilling\device_token.txt`; Desktop generates it on first boot before spawning the API child, and both processes share the same file. Admin-gated endpoints continue to use the stronger `X-Admin-Token` (operator passcode, 30 min TTL) and do **not** additionally require the device token. Reads are unauthenticated. If you're testing write endpoints against the API directly (curl, Postman), read the token out of the file and attach `X-Device-Token: <value>`.

The desktop owns the API lifecycle: on startup `ChildProcessSupervisor` spawns `ShowroomBilling.Api.exe` from the sibling `bin/Debug/net10.0/` directory and assigns it to a Windows Job Object with `KillOnJobClose`. Development launches use `DOTNET_ENVIRONMENT=Development`, `ASPNETCORE_ENVIRONMENT=Development`, and `--urls http://127.0.0.1:5108`, so VS Code/debug sessions use the dev/test DB and do not attach to a prod API on `5107`. Published EXE launches use `Production` and `--urls http://127.0.0.1:5107`. If the API is already running when the Desktop starts (e.g. you're running it separately to debug), the supervisor probes the configured API ports and skips spawning — the already-running process is left alone and will **not** be killed on Desktop exit. Flip `ChildProcesses:Enabled` (or `ChildProcesses:Api:Enabled`) to `false` in `appsettings.json` or `appsettings.Development.json` to opt out entirely. Config lives in [ChildProcessOptions.cs](src/ShowroomBilling.Desktop/Configuration/ChildProcessOptions.cs); supervisor logic in [ChildProcessSupervisor.cs](src/ShowroomBilling.Desktop/Services/ProcessSupervision/ChildProcessSupervisor.cs) + [JobObject.cs](src/ShowroomBilling.Desktop/Services/ProcessSupervision/JobObject.cs).

When `DesktopBootstrap:ConnectionMode` is `Server`, the desktop uses `ServerApiBaseUrl`, skips embedded API startup, and does not create/send the local `X-Device-Token`. Server API installs use `DeviceAuth:Mode=TrustedLan` plus trusted CIDRs/firewall scope for normal workstation writes; admin endpoints still require `X-Admin-Token`.

**Tally integration** — the API talks to Tally XML directly (in-process). There is no separate bridge process, no background job queue, and no polling. When the operator clicks **Push** on a bill, `BillService.PushAsync` transitions the bill to `posting`, calls `ITallyPoster` (which builds voucher XML, sends it via HTTP to the configured Tally endpoint, and parses the response), and settles the bill in `posted` or `failed`. First pushes create vouchers; edited-after-push bills alter the original Tally voucher by `MASTER ID` and fail safely if that old voucher cannot be identified. Master-data refresh (`/api/masters/refresh`) follows the same pattern: click → fetch → write → return. If the API crashes mid-post, `StuckPostingRecoveryHostedService` flips stranded `posting` rows back to `pending` on the next startup.

### Desktop shell at a glance

The desktop starts from local bootstrap config, then calls the API for the runtime bootstrap and kicks off a 15-second health poll against `/api/health/live` + `/api/health/masters` through `HealthApiClient`. The banner + status bar are driven off the snapshot: API down → red banner "Limited mode — cloud unavailable…" + `CLOUD DOWN`; otherwise `READY`. The status bar also shows `DB DEV`, `DB PROD`, or `DB UNSET` from the DB-owned identity marker. The window renders with custom chrome (32px caption, our own min/max/close); `Ctrl+1/2/3` switch Invoice/Bills/Settings, `?` opens the shortcuts overlay, `Esc` closes dialogs, and the F-key strip updates per tab. When `SystemState` is Limited, the screen region is replaced by `LimitedModeView` with a still-works / blocked split.

The header health-cluster (Cloud / Tally) also opens a full **System Health** dialog driven by the same `SystemHealthSnapshot`. Three cards render off real data: Cloud API (reachability), Tally (posting is manual — status shows neutral until the next push/refresh), and Master Data (overall freshness + per-type rows for companies / ledgers / stock-items / voucher-types with freshness / item count / fetched-at). Opening the dialog triggers a fresh poll; the footer shows last-checked timestamp plus **Refresh** and **Close**.

**Invoice tab** — editable header (invoice# / date / payment / 24kt rate / party), auto-appending line-item table (Item / Gross Wt / Less Wt / Unit / Karat / Wastage% / Labour / Diamond Rate / Extra / Rate (incl.) / Line Total (incl.) — Qty is dropped; the posted Quantity is the computed `NetWeight = max(0, Gross − Less)`). Item and Karat columns are editable ComboBoxes backed by Settings · Masters. Picking an item pre-fills Unit / Wastage % / Labour per gm; picking a karat drives purity. Pricing matches V1 exactly (`effective_rate = (rate_24kt × purity%/100) + wastage-component + labour-component` gated by the item master's `pricing_mode`; diamond items bypass the formula with a flat per-line inclusive **Diamond Rate**). Line totals are GST-inclusive; the footer Subtotal is the ex-GST back-calc as `Σline_inclusive / 1.03`, CGST + SGST at 1.5% each of that base. On post, each line's Tally `STOCKITEMNAME` resolves via `ResolveStockName`: first `KaratMaster.TallyItem`, else `ItemMaster.DefaultStockMappingLabel`, else the free-text name. Keyboard: `Ctrl+N` adds a row, `Ctrl+Del` removes the focused row, `Enter` / `Shift+Enter` walks line cells, `F2` focuses 24kt, `F3` the quick-add search, `F4` party, `F9` opens estimate preview, `Ctrl+S` saves (`POST/PUT /api/bills/drafts`). `CreateDraftAsync` reserves the invoice serial at save time (idempotency key `draft:{billId}`), so the `pending` bill carries its real number from the moment it is persisted. After save, the Print Preview dialog auto-opens in estimate mode; closing it clears the form and fetches the next serial preview. Save Bill is the only write action on the Invoice tab; Tally push lives exclusively on the **Bills** tab.

**Bills tab** — 4 summary tiles (PENDING / POSTING / POSTED / FAILED), filter bar (state / from / to), dense ListBox-backed table, paging footer. `F5` refresh, `PgDn/PgUp` paging, `Enter` / double-click opens the **Bill Details** dialog (summary, commercial breakdown, numbering, posting status). Dialog footer exposes state-aware actions — **Push** (pending → `/api/bills/{id}/push`, synchronous), **Retry** (failed → re-post), **Repost** (posted/failed → new push), **Revise** (pending), **Void** (pending/failed). Edited bills that were previously pushed update the old Tally voucher instead of creating a second voucher. `Ctrl+P` print preview, `Ctrl+S` push, `Ctrl+R` retry, `Ctrl+Shift+R` repost. The footer exposes **Push All Pending** (`POST /api/bills/push-pending`) when `StateFilter == "pending"`.

V1-parity context menu (admin-gated for destructive actions): right-clicking a row opens a state-aware menu — Open Details, **Edit** (Ctrl+Shift+E; any state except `posting` drops back to `pending` with `EditedAfterPush=true`, invoice number preserved), Push / Retry / Repost / Revise / Print / Copy Invoice #, **Change Bill Number…** (admin; dry-run pass surfaces `LeavesGap` / `TallyDiverges` / `ReservationOrphaned` warnings), **Mark as Pushed…** / **Mark as Pending…** (admin; reason ≥4 chars; local-only state override, no Tally traffic), Void…, **Delete Local…** (admin; hard-deletes bill + revisions). Admin endpoints require `X-Admin-Token`; if no unlock session is active, `BillsViewModel.AdminUnlockHandler` opens the admin dialog and awaits its close before retrying.

**Settings tab** — top meta strip (source / summary / updated-at / UNSAVED CHANGES chip / Refresh|Edit or Discard|Save All), left-nav (Database / Connection / Invoice / Ledgers / Masters / Advanced), right detail pane rendering the current `EffectiveSettingsResponse` section-by-section. Database controls the desktop API connection mode (`Server` vs `LocalEmbedded`) and the local embedded DB override. Invoice expands into Company / Bank / Numbering / Print Copies / Layout / T&C. Pane defaults to read-only; **Edit** switches inputs to editable; `SettingsDraft` tracks edits. **Save All** runs client-side validation and `PUT /api/settings` with the full `EffectiveCloudSettingsDto`. **Active Company** sub-section: ComboBox fed by `GET /api/masters/companies`, inline **Fetch** (cached cloud snapshot), **Refresh from Tally** (`POST /api/masters/refresh` with `MasterType=companies`), **Set Active** (`POST /api/settings/company/select` — reloads effective settings on success). Ledger mapping fields (Sales / Cash / Credit-Debit / CGST / SGST / Round-off / Discount) and Sales Voucher Type use ComboBoxes backed by `/api/masters/ledgers` and `/api/masters/voucher-types`. Masters pane: editable Bill Items (name / unit / category / pricing mode / wastage % / labour-per-gm / Tally stock-mapping label) and Karats (label / purity % / Tally stock item) grids; Tally stock items have Fetch / Refresh-from-Tally buttons. JSON matches V1's Python dataclasses 1:1 and is serialized back into `MasterDataSettingsDto.ItemMasterDataJson` / `KaratMappingDataJson` on Save All. Advanced surfaces `CloudOwnedCategories` / `LocalOnlyCategories`, the Admin-mode hint (press `~` to unlock), and a **Logs & Diagnostics** block (Log folder / App data / Install folder with Open buttons).

**Print Layout** (own left-nav entry) loads via `GET /api/settings/print-layout` (margins + logo/signature placements) in parallel with `GET /api/print-assets`. Each of Logo and Signature has a Browse… button (`.png`/`.jpg`/`.jpeg` ≤ 2 MB, base64-encoded, POSTs `/api/print-assets` with correct `assetKind`), a Delete button, and four placement fields (OffsetX / OffsetY / Width / Height in cm). Save persists via `PUT /api/settings/print-layout`. A live print preview docks on the right for the **Invoice** and **Print Layout** sections — renders the canned sample bill via QuestPDF at 96 DPI, debounced 300 ms, reflects unsaved edits (including uploaded-but-not-yet-saved logo/signature bytes). See `docs/07_printing_spec.md` §5.4.

**Admin unlock** is reachable anywhere via `~`. `AdminUnlockDialog` drives `IAdminApiClient` against `/api/admin/*` — Unlock (when locked), Set/Change passcode (min 4 chars + confirmation), and once unlocked, Active Draft Locks (`DataGrid` fed by `GET /api/draft-leases/active` with a Force-release row action requiring a reason and writing an audit event). The session token lives in `AdminTokenStore` (30-minute TTL with auto-expiry) and is attached as `X-Admin-Token` by `DraftLeaseApiClient` for admin-gated endpoints. Lock / Logout revokes the server-side token.

**Printing** is wired through the `ShowroomBilling.Printing` library (QuestPDF templates — header / party / lines / totals / notes+T&C / signature) and a WPF **Print Preview** dialog. Every printed document is a Tax Invoice; all printouts show `TAX INVOICE` in the header. `F9` on Invoice opens the preview against the live pending bill; BillDetails' **Print…** opens it against the loaded bill; a successful **Save Bill** auto-opens the preview. The preview pane renders at 110 DPI in a background task; the sidebar holds Copies (Original/Duplicate/Triplicate) and Printer combobox. Footer: **Save as PDF** → `SaveFileDialog` defaulting to `{invoice#}.pdf`; **Print** (Enter/IsDefault) renders at 300 DPI into a WPF `FixedDocument` via `PrintQueue.CreateXpsDocumentWriter`. Last-used printer + PDF directory persist to `%APPDATA%\ShowroomBilling\print-preferences.json`. Printing does not mutate bill state.

Design-system surfaces:

- tokens in [src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml](src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml)
- control styles in [src/ShowroomBilling.Desktop/Resources/Styles.xaml](src/ShowroomBilling.Desktop/Resources/Styles.xaml)
- design reference: [docs/15_ui_design_reference.md](docs/15_ui_design_reference.md), mockup bundle in [docs/design/](docs/design/)

## VS Code F5

- shared VS Code debug config is checked into [.vscode/launch.json](.vscode/launch.json)
- build tasks are in [.vscode/tasks.json](.vscode/tasks.json)
- recommended debug target: `Foundation: Desktop + API`
- press `F5` in VS Code and select the compound launch target
- recommended extensions are in [.vscode/extensions.json](.vscode/extensions.json)

## Logs

Both hosts (API, Desktop) write structured rolling-file logs via `ShowroomBilling.Application.Logging.RollingFileLoggerProvider` (alias `File`). Files live under `%APPDATA%\ShowroomBilling\logs` — the same folder the Settings → Advanced → Logs & Diagnostics **Open** button points at.

- file naming: `{prefix}-{yyyyMMdd}.log` (prefixes: `api-`, `desktop-`); within a day, size-cap rollover at `FileSizeLimitMB` (default 20 MB) appends `-1`, `-2`, …
- line shape: `{ISO timestamp} [{LEVEL3}] [{scope key=values}] {Category}: {message}` plus optional exception block; the `CorrelationId` rides as a scope key on every API request, so desktop → API traces are greppable by a single id
- retention: default 30 days, runs on provider startup + on each daily rollover; files with `LastWriteTime` older than the cutoff are deleted
- configuration lives under `Logging:File` in each host's `appsettings.json` — `Enabled`, `Directory`, `FilePrefix`, `RetentionDays`, `FileSizeLimitMB`, `MinLevel`
- writes are non-blocking: `RollingFileLogger` drops lines into a bounded `BlockingCollection<string>` (cap 4096) and a dedicated background task drains it to disk; graceful shutdown flushes up to a 2 s drain window via `ILoggerProvider.Dispose`

## Settings-storage contract

- local desktop config is limited to bootstrap endpoint, device identity, and workstation-local UX preferences
- shared settings remain API/backend-owned and are surfaced through API endpoints
- no local SQLite or other local durable shared-settings store is introduced

Full note: [docs/14_settings_storage_contract.md](docs/14_settings_storage_contract.md)
