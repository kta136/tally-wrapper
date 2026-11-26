# Development Setup

## Prerequisites

- .NET SDK `10.0.202`
- Windows for the WPF desktop project
- Docker (a local engine, or a remote Docker context), for the throwaway PostgreSQL 18 containers that `tools\run-scratch.ps1` and the database tests start
- `dotnet-ef`, installed globally: `dotnet tool install --global dotnet-ef`
- Native OpenBao CLI (`bao`) matching the server version for shared-environment secret retrieval and rotation

## Database

Development uses a throwaway PostgreSQL 18 container: [`tools\run-scratch.ps1`](#run-against-a-scratch-database) starts one for the app, and the [database tests](#database-tests) start their own. Real connection strings are not committed. Only the API holds one; the Desktop has none and no screen for one. A private copy for a development API may live in an ignored environment file (`src/ShowroomBilling.Api/appsettings.Development.json`), user secrets, or the `SHOWROOM_BILLING_POSTGRES` environment variable, which wins over every saved connection. A connection string is in the Npgsql keyword form; a `postgres://` URL is refused.

The production database, its secret and how that is retrieved and rotated, and the shared persistent test database are described in one place, [docs/11 §4](docs/11_deployment_and_ops.md#4-postgresql-deployment). That test database is not a connection for the test suite: the fixture creates and drops databases, which its role may not do ([Database tests](#database-tests)).

Set `Database:AutoMigrateOnStartup=true` only in private local environment files.

Migrations are applied with the EF tools (`dotnet-ef`, installed globally). Pass the target's connection string explicitly; never rely on EF environment-name resolution to select the database:

```powershell
dotnet ef database update --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --connection "<connection-string>"
```

`tools\run-scratch.ps1` does this for the scratch database. For production, migrations are applied by hand before a new build is started; the procedure is in [docs/11 §11](docs/11_deployment_and_ops.md#11-upgrade-flow).

The API's committed `appsettings.json` holds only a placeholder connection string (`Host=localhost;Port=5432;Database=tally_wrapper;Username=postgres;Password=postgres`).

If the database is unreachable, the API and the Desktop still start, but DB-backed settings and migrations remain unavailable until the database is reachable. The Desktop then shows the limited-mode screen and tells the operator to check the database connection in the tray on the server PC.

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

The Desktop never opens a PostgreSQL connection and has no database settings. The server's database connection is tested and saved from the Tally Wrapper tray on the server PC, and takes effect when the API service restarts.

## Build

```powershell
dotnet restore ShowroomBilling.sln
dotnet build ShowroomBilling.sln
dotnet test --solution ShowroomBilling.sln
```

`global.json` selects Microsoft Testing Platform for xUnit 4. Use the explicit
`--solution` or `--project` form with `dotnet test`; the older positional form
is not accepted in MTP mode.

Run the same local quality gates used by the Windows CI job:

```powershell
dotnet list ShowroomBilling.sln package --vulnerable --include-transitive
dotnet format style ShowroomBilling.sln --verify-no-changes --no-restore
dotnet build ShowroomBilling.sln --configuration Release --no-restore -warnaserror
.\tools\Test-Coverage.ps1 -NoBuild
```

`Test-Coverage.ps1` builds by default when used on its own; pass `-NoBuild` only after building the same configuration. It runs both test projects, merges their reports per source line and enforces a 64% line floor on hand-written code (generated EF migrations are excluded; the floor is the script's default). CI splits that across three jobs: Windows builds and runs every test that needs no database (`-Tests WithoutDatabase`), Ubuntu runs the database tests (`-Tests DatabaseOnly`) because a Windows runner cannot run the PostgreSQL container, and a last job merges both halves' reports and enforces the floor (`-Tests None`). Each half publishes its TRX and Cobertura files as artifacts. Package versions are centrally pinned in `Directory.Packages.props`; the package-audit command must stay clean before dependency updates are merged.

### Database tests

Every test that touches the database runs on PostgreSQL 18, the production version; there is no in-memory stand-in. A plain `dotnet test` runs them with everything else. `PostgresFixture` starts one server for the run the first time a test asks for a database, migrates a template database once, and gives each test its own copy of it, dropped afterwards. The tests never read the local or production Oracle VPS connection strings.

The server is the first of these that applies:

1. `SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION`, when set: an already-running PostgreSQL 18 whose role may create databases, for example `Host=<host>;Port=<port>;Database=postgres;Username=postgres;Password=<password>`.
2. A throwaway `postgres:18` container started with the `docker` command, when the current Docker context is a remote engine (`ssh://` or `tcp://`, such as the `proxmox-docker` context). Testcontainers can only drive a local engine. The container is stopped when the run ends, and stops itself after two hours if the run was killed.
3. A Testcontainers container on the local Docker engine. This is what CI uses.

With no Docker and no connection string the database tests fail, each saying what is missing; they are not skipped. They carry the trait `Category=Postgres`, so the two halves can be run separately:

```powershell
dotnet test --project tests/ShowroomBilling.Tests/ShowroomBilling.Tests.csproj --filter "Category=Postgres"
dotnet test --project tests/ShowroomBilling.Tests/ShowroomBilling.Tests.csproj --filter "Category!=Postgres"
```


## Run the API

```powershell
dotnet run --project src/ShowroomBilling.Api
```

This uses whatever database connection the PC has saved, or `SHOWROOM_BILLING_POSTGRES` when it is set. On a PC that also runs the real app, use the [scratch run](#run-against-a-scratch-database) instead.

Key endpoints:

- `http://localhost:5108/openapi/v1.json` (the OpenAPI document; Development only, there is no browsable Swagger page)
- `http://localhost:5108/api/health/live`
- `http://localhost:5108/api/health` (database reachable, its identity and pending migrations, from a check at most 5 seconds old, and per-list snapshot freshness; what the 15-second poll asks)
- `http://localhost:5108/api/masters/companies` (desktop read of the whole current snapshot; also `ledgers`, `stock-items`, `voucher-types`)
- `http://localhost:5108/api/masters/refresh` (synchronously fetches from Tally and writes the snapshot — the only way masters get updated; there is no background polling)
- `http://localhost:5108/api/numbering/preview` (next visible invoice number; does not reserve)
- `http://localhost:5108/api/bills` (bill search; POST `/drafts` to create; `PUT /drafts/{id}` to update; POST `{id}/push|revise|void`)
- `http://localhost:5108/api/bills/push-pending` (synchronously posts every pending bill to Tally, one at a time)
- `http://localhost:5108/api/bills/{id}/audit` (bill event trail, ordered by timestamp)
- `http://localhost:5108/api/bills/by-operation/{operationId}` (the bill a create with that `clientOperationId` made; used by the bill hand-over)
- `http://localhost:5108/api/bills/{id}/retry` (POST; re-run the sync push after a failure; answers with the bill's state and last-post metadata)
- `http://localhost:5108/api/bills/{id}/repost` (POST; push again for a posted/failed bill; same answer)
- `http://localhost:5108/api/bills/synthetic-batch` (POST; admin-gated via `X-Admin-Token`; generates N `pending` bills with backfilled timestamps — see [docs/17_synthetic_batch.md](docs/17_synthetic_batch.md))

## The server (API + tray)

The server is a single EXE that carries the API, installs or repairs its Windows Service and stays as the tray:

```powershell
.\tools\publish-server-tray.ps1
```

It produces `publish\server\TallyWrapper.Server.exe`. Installing it, what the install sets, upgrading and removing it are in [docs/11 §3](docs/11_deployment_and_ops.md#server-install); the tray in [§5](docs/11_deployment_and_ops.md#5-server-tray); pointing a workstation at the server in [§2](docs/11_deployment_and_ops.md#server-address). Do not run the tray exe on a development PC: starting it installs the service.

A Desktop with no saved server address uses `DesktopBootstrap:ApiBaseUrl` from its `appsettings.json` (`http://localhost:5107`, or `http://localhost:5108` in Development). The environment variable `SHOWROOM_DESKTOP_DesktopBootstrap__ApiBaseUrl` overrides both that and a saved address.

## Run the desktop shell

The Desktop is a client: it needs an API that is already running and never starts one. With nothing listening at its address it opens on the limited-mode screen.

- `.\tools\run-scratch.ps1` starts the API on `127.0.0.1:5108` and then the Desktop, both on a throwaway database, and stops the API when the app closes. See [Run against a scratch database](#run-against-a-scratch-database).
- In VS Code, the `Desktop + API` compound starts both. The `Desktop` configuration alone needs an API you started yourself.
- By hand, start the API as above and then:

```powershell
$env:DOTNET_ENVIRONMENT='Development'
dotnet run --project src/ShowroomBilling.Desktop
```

A Desktop started by `dotnet run` or F5 reads this PC's real settings folder. If the PC has a server address saved in Settings → Server, a Desktop started by `dotnet run` uses that address in place of `http://localhost:5108`, so on a PC that also runs the real app it talks to the real server. `run-scratch.ps1` avoids this by giving the Desktop an empty settings folder, and the VS Code `Desktop` launch configuration by setting `SHOWROOM_DESKTOP_DesktopBootstrap__ApiBaseUrl` to the development API; for `dotnet run`, set that variable for the session.

**Device auth on write paths.** Every mutating API endpoint (bill create/update/push/retry/repost/void/revise, settings PUT, masters refresh, print-asset upload/delete) passes device auth. The server install runs with `DeviceAuth:Mode=TrustedLan`: a request from the server PC itself or from the trusted LAN range needs no token, and a workstation sends none. A development API runs in the default `LocalFile` mode and needs the `X-Device-Token` header, a 32-byte random secret in `%LOCALAPPDATA%\ShowroomBilling\device_token.txt`; whichever of the API and the Desktop starts first creates the file, and both read it. The Desktop attaches the header only when its API address is loopback. Admin-gated endpoints continue to use the stronger `X-Admin-Token` (operator passcode, 30 min TTL) and do **not** additionally require the device token. One call needs both: saving an edit to a `posted` bill (`PUT /api/bills/drafts/{id}`) is a device-auth endpoint that also asks for `X-Admin-Token`, and answers `403` without it. Reads are unauthenticated. If you're testing write endpoints against a development API directly (curl, Postman), read the token out of the file and attach `X-Device-Token: <value>`.

**Tally integration** — the API talks to Tally XML directly (in-process). There is no bridge, posting queue, or polling. Push first checks that Tally is reachable and the configured company is open. If preflight fails, the API returns `503` and leaves the state unchanged. After the bill enters `posting`, the API makes exactly one voucher HTTP attempt: explicit Tally acceptance/rejection settles in `posted`/`failed`; a timeout, transport error, unreadable response, or API crash settles in `reconciliation_required`. First pushes create vouchers; edited-after-push bills alter the original by `MASTER ID` and the date Tally holds for it, read from Tally first ([docs/05 §4](docs/05_tally_integration_contract.md)). Safe master/health reads retain a two-retry transport pipeline, but voucher writes do not. Reconciliation requires checking Tally and using admin Mark as Pushed or Mark as Pending.

### Desktop shell at a glance

The desktop reads its server address and loads settings and a health check from the API (it does not wait for the address to answer first: with no answer it opens on the limited-mode screen and the health poll brings it back), which also brings the database identity for the status bar, and kicks off a 15-second health poll through `HealthApiClient`. If the server reports that initial setup is still needed (`RequiresInitialSetup`) and this workstation has not finished it, the first-launch setup dialog opens with one form, "Showroom basics"; it has no database step. Every tick hits `/api/health/live` and `/api/runtime/health`, and the server answers the second from a check of PostgreSQL that is at most 5 seconds old; Tally-company health and `/api/health/masters` are requested on startup, explicit health refreshes, and a slower 30-minute scheduled probe. The banner + status bar are driven off the snapshot: server not answering → red banner "Cloud unavailable — the desktop can prepare bills but cannot save. Retrying…" + `CLOUD DOWN`; server cannot reach its database → "The server cannot reach its database. On the server PC, open the Tally Wrapper tray and check the database connection." + `DB DOWN` (once two polls in a row say so; the database pill turns red on the first); database behind its migrations → `DB BEHIND`; otherwise `READY`. The Desktop does not open a settings screen by itself in any of these cases. The status bar also shows `DB DEV`, `DB PROD` or `DB UNSET`. The desktop and server dashboard run Per-Monitor V2 DPI-aware; the desktop's custom chrome derives maximized bounds from the active monitor's work area so the taskbar does not cover the F-key or status strips. `Ctrl+1/2/3` switch Invoice/Bills/Settings, `?` opens the shortcuts overlay, `Esc` closes dialogs, and the F-key strip updates per tab. When `SystemState` is Limited, the screen region is replaced by `LimitedModeView` with a still-works / blocked split and a **Server Settings** button that opens Settings → Server. See [UI Design Reference §3.7](docs/15_ui_design_reference.md#37-dpi-and-monitor-work-areas).

The header health-cluster (Cloud / Tally) also opens a full **System Health** dialog driven by the same `SystemHealthSnapshot`. Three cards render off real data: Cloud API (reachability), Tally (reachability + configured active company), and Master Data (overall freshness + per-type rows for companies / ledgers / stock-items / voucher-types with freshness / item count / fetched-at). Opening the dialog triggers a fresh poll; the footer shows last-checked timestamp plus **Refresh** and **Close**.

**Invoice tab** — editable header (invoice# / date / payment / 24kt rate / party), auto-appending line-item table (Item / Gross Wt / Less Wt / Unit / Karat / Wastage% / Labour / Diamond Rate / Extra / Rate (incl.) / Line Total (incl.) — Qty is dropped; the posted Quantity is the computed `NetWeight = max(0, Gross − Less)`). Item and Karat columns are editable ComboBoxes backed by Settings · Masters. Picking an item pre-fills Unit / Wastage % / Labour per gm; picking a karat drives purity. Pricing matches V1 exactly (`effective_rate = (rate_24kt × purity%/100) + wastage-component + labour-component` gated by the item master's `pricing_mode`; diamond items bypass the formula with a flat per-line inclusive **Diamond Rate**). Line totals are GST-inclusive; the footer Subtotal is the ex-GST back-calc as `Σline_inclusive / 1.03`, CGST + SGST at 1.5% each of that base. The discount footer has linked **Discount** and **Final** amount inputs: typing either one updates the other and recalculates the grand total immediately. On post, each line's Tally `STOCKITEMNAME` resolves via `ResolveStockName`: first `KaratMaster.TallyItem`, else `ItemMaster.DefaultStockMappingLabel`, else the free-text name. Keyboard: `Ctrl+N` adds a row, `Ctrl+Del` removes the focused row, `Enter` / `Shift+Enter` walks line cells, `F2` focuses 24kt, `F3` the quick-add search, `F4` party, `F9` opens estimate preview, `Ctrl+S` saves (`POST/PUT /api/bills/drafts`), `Ctrl+Shift+N` (the **Clear** / **New bill** button) empties the form for a new bill, leaving a saved bill opened with Edit as it is and asking first when there are changes not saved. `CreateDraftAsync` takes the invoice serial at save time, in the fiscal year of the **bill date** and in the same transaction as the bill, so the `pending` bill carries its real number from the moment it is persisted. Save is replay-safe: the form keeps one `ClientOperationId` per unsaved bill across retries, so a save whose response was lost returns the same bill instead of numbering a second one; updates send the revision they opened (`expectedRevisionId`) and are refused if another counter saved in between. Saving an edit to a `posted` bill sends the admin token, and asks for admin unlock first if the session has expired; without it the bill is not saved. `Ctrl+S` / `F9` first commit the cell being typed in. Save asks first ("Save “…” as typed?") when a line's item is not one of the saved items, which is also what a name cut short by `Ctrl+S` looks like ([UI Design Reference](docs/15_ui_design_reference.md)); `Ctrl+N` and `Ctrl+Del` do nothing while that or any other question or dialog is open. Clearing the form resets the bill date to today and payment to Cash. After a save the form is emptied at once and the next serial preview fetched; the Print Preview dialog, or the direct print, is then made from the bill as saved. Save Bill is the only write action on the Invoice tab; Tally push lives exclusively on the **Bills** tab.

**Bills tab** — filter bar (state / from / to), dense ListBox-backed table, selection action bar, paging footer. `Ctrl`/`Shift` multi-select works in the table and is mirrored by the row checkboxes. `F5` refresh, `PgDn/PgUp` paging, `Enter` / double-click opens the **Bill Details** dialog (summary, line items, totals, timeline). Dialog footer exposes state-aware actions — **Push** (pending → `/api/bills/{id}/push`, synchronous), **Retry** (failed → re-post), **Repost** (posted/failed → sends a *new* voucher; only after the original was deleted in Tally), **Revise** (pending; the new bill gets its own number), **Void** (pending/failed; asks for confirmation first, and is refused while Tally still holds the voucher). Revise and Void are in this dialog only. The dialog takes keyboard focus when it opens, and the Bills list ignores keys while any dialog is open. Filter **Apply**, **Clear** and `Enter` in the search box reload from the first page; search is case-insensitive. Push/retry/repost controls are disabled when the latest Tally health says Tally is unavailable, and they force-refresh Tally health before calling the bills API. Edited bills that were previously pushed update the old Tally voucher instead of creating a second voucher. `Ctrl+P` print preview, `Ctrl+S` push, `Ctrl+R` retry, `Ctrl+Shift+R` repost. The footer exposes **Push All Pending** (`POST /api/bills/push-pending`) when `StateFilter == "pending"`.

V1-parity context menu (admin-gated for destructive actions): right-clicking a row opens a state-aware menu — Open Details, **Edit** (Ctrl+Shift+E, also on the toolbar; for `pending`, `failed` and `posted` bills; saving drops the bill back to `pending`, invoice number preserved; `EditedAfterPush=true` only when Tally holds the voucher, so the next push Alters it, while a bill whose first push was rejected is pushed again as a Create. Editing a `posted` bill needs admin: Edit opens the admin-unlock dialog first if the session is locked, and the server answers `403 Admin session required` to a save without one. A `failed` bill needs no admin), Retry Push / Push to Tally / Repost to Tally / Print / Copy Invoice Number, **Change Bill Number…** (admin; dry-run pass surfaces `LeavesGap` / `TallyDiverges` warnings; renumbering a bill Tally holds moves it to `pending` as an edit so the next push Alters the same voucher to the new number), **Mark as Pushed…** / **Mark as Pending…** (admin; reason ≥4 chars; local-only state override, no Tally traffic), **Delete Local…** (admin; hard-deletes bill + revisions). Right-clicking inside a multi-selection preserves it, so Mark as Pushed, Mark as Pending, Print, and Delete Local operate on the selected rows when all selected rows are eligible; Change Bill Number and Edit remain single-row. Admin endpoints require `X-Admin-Token`; if no unlock session is active, `BillsViewModel.AdminUnlockHandler` opens the admin dialog and awaits its close before retrying.

**Settings tab** — top meta strip (source / summary / updated-at / UNSAVED CHANGES chip / Refresh|Edit or Discard|Save All), left-nav (Connection / Invoice / Print Layout / Ledgers / Masters shared by every counter; Server / Hand-over / Advanced for this PC only; Tally check under Checks; plus Admin while admin is unlocked), right detail pane rendering the current `EffectiveSettingsResponse` section-by-section. Server holds this workstation's server address, with **Test server**, **Find server** and **Save and restart Billing**; nothing in it is admin-gated, and it shows nothing about the database. Invoice expands into Company / Bank / Numbering / Print Copies / Layout / T&C. Pane defaults to read-only; **Edit** switches inputs to editable; `SettingsDraft` tracks edits. **Save All** runs client-side validation and `PUT /api/settings` with the full `EffectiveCloudSettingsDto`. **Active Company** sub-section: ComboBox fed by `GET /api/masters/companies`, one **Refresh from Tally** button (`POST /api/masters/refresh` with `MasterType=companies`, then reloads the cached snapshot into the dropdown), **Set Active** (`POST /api/settings/company/select` — reloads effective settings on success). Ledger mapping fields (Sales / Cash / Credit-Debit / CGST / SGST / Round-off / Discount) and Sales Voucher Type use ComboBoxes backed by `/api/masters/ledgers` and `/api/masters/voucher-types`. Masters pane: editable Bill Items (name / unit / category / pricing mode / wastage % / labour-per-gm / Tally stock-mapping label) and Karats (label / purity % / Tally stock item) grids; Tally stock items have one Refresh-from-Tally button that re-pulls and reloads the list. JSON matches V1's Python dataclasses 1:1 and is serialized back into `MasterDataSettingsDto.ItemMasterDataJson` / `KaratMappingDataJson` on Save All. Advanced surfaces `CloudOwnedCategories` / `LocalOnlyCategories`, the Admin-mode hint (press `~` to unlock), and a **Logs & Diagnostics** block (Log folder / App data / Install folder with Open buttons). **Tally check** has one button, **Compare with Tally** (`GET /api/tally-check/compare`), which sets this year's bills against the sales vouchers in Tally and lists what differs, with filter pills and an **Open bill** button that opens Bill details; it changes nothing, and its result is kept only while the app is open ([05 §4](docs/05_tally_integration_contract.md#settings--tally-check)). `TallyCheckViewTests` lays it out in each state; with `TALLYWRAPPER_VIEW_SNAPSHOTS` set it saves `TallyCheck-*.png`.

**Print Layout** (own left-nav entry) loads via `GET /api/settings/print-layout` in parallel with `GET /api/print-assets`. Logo and Signature retain Browse/Delete plus four placement fields. Watermark adds Browse/Delete, full-page A4 X/Y/width/height (cm), and opacity; all three accept `.png`/`.jpg`/`.jpeg` up to 2 MiB through `/api/print-assets`. The structured designer supports native drag/drop and keyboard Move Up/Down, optional-section visibility, 0–20 mm before/after gaps, compact/standard/comfortable density, 0–4 pt invoice border, and a contiguous “Pin bottom from here” boundary. Reset restores the historical order with GST as the pin boundary. Save persists the shared JSON via `PUT /api/settings/print-layout`. The docked live preview renders the canned sample through QuestPDF at 120 DPI with a 300 ms debounce and reflects unsaved values and pending logo/signature/watermark bytes. See `docs/07_printing_spec.md` §§5.2–5.4.

**Admin unlock** is reachable via `~` anywhere outside a text box while no dialog is open. `AdminUnlockDialog` drives `IAdminApiClient` against `/api/admin/*` — Unlock (when locked) and Set/Change passcode (min 6 chars + confirmation; changing an existing passcode needs an unlocked session and is throttled like unlock). The session token lives in `AdminTokenStore` (30-minute TTL; expiry hides admin-only UI) and is attached as `X-Admin-Token` on admin-gated endpoints. Lock / Logout revokes the server-side token.

**Printing** is wired through the `ShowroomBilling.Printing` library (QuestPDF templates — header / party / lines / totals / notes+T&C / signature) and a WPF **Print Preview** dialog. Every printed document is a Tax Invoice; all printouts show `TAX INVOICE` in the header. `F9` on Invoice opens the preview against the live pending bill; BillDetails' **Print…** opens it against the loaded bill; a successful **Save Bill** auto-opens the preview of the bill exactly as saved (not the live form). The preview pane renders at 110 DPI in a background task; the sidebar holds Copies (Original/Duplicate/Triplicate) and Printer combobox. Footer: **Save as PDF** → `SaveFileDialog` defaulting to `{invoice#}.pdf`; **Print** (Enter/IsDefault) sends the pages as text and lines in a WPF `FixedDocument` via `PrintQueue.CreateXpsDocumentWriter`, or as 600 DPI pictures for a bill that cannot be drawn that way ([docs/07 §1.5](docs/07_printing_spec.md)). Last-used printer + PDF directory persist to `%APPDATA%\ShowroomBilling\print-preferences.json`. Printing does not mutate bill state.

Design-system surfaces:

- tokens in [src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml](src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml)
- control styles in [src/ShowroomBilling.Desktop/Resources/Styles.xaml](src/ShowroomBilling.Desktop/Resources/Styles.xaml)
- design reference: [docs/15_ui_design_reference.md](docs/15_ui_design_reference.md), mockup bundle in [docs/design/](docs/design/)

## Run against a scratch database

On a PC that also runs the real app, the dev build must not find the saved server address or database connection, and test bills must not reach TallyPrime. This starts it so that none of that can happen:

```powershell
.\tools\run-scratch.ps1
```

It needs Docker (the `proxmox-docker` context unless `-DockerContext` names another) and the globally installed `dotnet-ef`. Everything it creates is under `artifacts\scratch`, which git ignores:

- a `postgres:18` container named `tw-scratch-postgres` with a random password, migrated to the current schema and marked `DEV`;
- empty settings folders, handed to the app through `SHOWROOM_BILLING_APPDATA` and `SHOWROOM_BILLING_LOCALAPPDATA`, with `SHOWROOM_BILLING_POSTGRES` pointing at the container. The API gives that variable precedence over every saved connection string;
- a stand-in Tally (`tools\scratch-tally.ps1`) on `127.0.0.1:9000`, which is where a new database expects Tally. It serves three companies (the first is the one a new database names as active, so Push works at once), nine ledgers, five stock items and three voucher types, and accepts vouchers. A bill whose party name or note contains `FAIL` is refused with a line error (the bill becomes `failed`); one containing `UNKNOWN` is kept but answered with a server error (the bill needs reconciliation). It also answers "which voucher has this id" and "which vouchers carry this number" from the vouchers of the current run, so Mark as Pushed after `UNKNOWN`, then an edit and a push, alters that voucher. It matches an alter the way TallyPrime does, by id and the voucher's current date, and like TallyPrime it creates a new voucher when they do not match. `artifacts\scratch\tally.log` lists what the app sent;
- the dev build's API on `127.0.0.1:5108`, started by the script and stopped when the app closes. The Desktop it then starts finds that API through its Development address.

The script refuses to start the app when something already listens on port 9000 (probably TallyPrime; pass `-AllowRealTally` if that is intended) or on the dev API port 5108 (the Desktop would connect to that API, whatever database it uses, in place of the one the script starts).

The app starts like a fresh install: no admin passcode, placeholder company. The database keeps its data between runs until the container stops. `-NoLaunch` prepares without starting the app, `-Reset` starts over with an empty database, `-Remove` deletes the container and the folder. A plain `dotnet run` or F5 does none of this: the API uses whatever database connection this PC has saved, and the Desktop whatever server address.

**Starting from a copy of real data.** `-RestoreFrom C:\path\copy.dump` loads a dump made with `pg_dump --format=custom --no-owner` (the command in [docs/11 §11](docs/11_deployment_and_ops.md)) into a new scratch database. The script never connects to the database the dump came from; taking the dump is a separate step for whoever holds that database's credentials. After the restore it:

- marks the copy `DEV` and deletes the admin sessions that came with it (the admin passcode itself is the copy's);
- points the copy's Tally connection at the stand-in, and from then on refuses to start the app while that connection is anything else;
- applies the migrations the copy has not had. With a dump of production this rehearses the next release on real data, including the tables a destructive migration copies aside;
- writes the copy's company and master lists to `artifacts\scratch\tally-masters.json`, which the stand-in Tally serves, so "Refresh from Tally" returns the same lists and the company in Settings counts as open.

Such a copy holds real customer data on the Docker host until `-Reset` or `-Remove`. Keep the dump file outside `artifacts\scratch`, and do not hand out anything printed from the copy: its invoice numbers continue the real series.

## VS Code F5

- shared VS Code debug config is checked into [.vscode/launch.json](.vscode/launch.json)
- build tasks are in [.vscode/tasks.json](.vscode/tasks.json)
- recommended debug target: `Desktop + API`. It starts the API on `127.0.0.1:5108` and then the Desktop
- press `F5` in VS Code and select the compound launch target
- the `Desktop` configuration alone starts no API; with none running it opens on the limited-mode screen
- the Desktop does not wait for the API's port: if it opens before the API of the compound is listening, it shows the limited-mode screen until the next health poll (15 seconds) or **Refresh** in System Health
- both use this PC's real settings folders (see [Run the desktop shell](#run-the-desktop-shell)); for a run that cannot touch anything real, use `tools\run-scratch.ps1`
- recommended extensions are in [.vscode/extensions.json](.vscode/extensions.json)

## Logs

Both hosts (API, Desktop) write rolling-file logs through Serilog's file sink, set up by `LoggingExtensions.AddRollingFile` in `ShowroomBilling.Application.Logging`. The Desktop's files live under `%APPDATA%\ShowroomBilling\logs` on the workstation — the folder the Settings → Advanced → Logs & Diagnostics **Open** button points at. The server's API writes to `C:\ProgramData\ShowroomBilling\logs`; an API started by hand in development writes to the same `%APPDATA%` folder as the Desktop.

- file naming: `{prefix}-{yyyyMMdd}.log` (prefixes: `api-`, `desktop-`); within a day, size-cap rollover at `FileSizeLimitMB` (default 20 MB) continues in `{prefix}-{yyyyMMdd}_001.log`, `_002`, …. A second copy of the same host running at the same time (a dev API beside the installed one, say) cannot share the file, so it writes to the next numbered one too
- line shape: `{ISO timestamp} [{LEVEL3}] [{scope key=values}] {Category}: {message}` plus optional exception block (`LogLineFormatter`); the `CorrelationId` rides as a scope key on every API request, so desktop → API traces are greppable by a single id
- retention: default 30 days, judged by the date in the file name; old files are removed whenever a file is opened (start-up, the daily rollover, a size rollover)
- configuration lives under `Logging:File` in each host's `appsettings.json` — `Enabled`, `Directory`, `FilePrefix`, `RetentionDays`, `FileSizeLimitMB`, `MinLevel`
- the API's log levels start from `LogLevelDefaults` in code (`Default` Information, `Microsoft.AspNetCore` Warning), because the installed server is published without an `appsettings.json`. A settings file or an environment variable such as `Logging__LogLevel__Default=Debug` still overrides them; on the server that is a line in the service's environment
- writes are non-blocking: lines go into a bounded queue (4096; a full queue drops the line) and a background thread writes each one to the file as it arrives; disposing the provider at shutdown writes out whatever is still queued

## Settings-storage contract

- local desktop config is limited to the server address, device identity, workstation-local UX preferences, and this PC's bill hand-over key (DPAPI-protected, [docs/18](docs/18_bill_handover.md))
- shared settings remain API/backend-owned and are surfaced through API endpoints
- no local SQLite or other local durable shared-settings store is introduced

Full note: [docs/14_settings_storage_contract.md](docs/14_settings_storage_contract.md)
