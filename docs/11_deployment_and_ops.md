# Deployment and Operations

This document describes how Tally Wrapper is deployed and operated.

---

## 1. Deployment topology

### Components

- WPF desktop app on each counter workstation. It is a client of the server API and never starts an API of its own
- one ASP.NET Core API, a Windows Service **on the same machine as TallyPrime** (reaches Tally's XML endpoint over localhost)
- `ShowroomBilling.ServerTray` on the Tally server: the installer for that service, and its companion in the logged-in user's taskbar notification area
- PostgreSQL 18 reachable from the API server. Local development can use a [scratch database](../DEV_SETUP.md#run-against-a-scratch-database).
- TallyPrime running on the Tally host

No separate bridge process. No SignalR hub. No job queue.

### Communication paths

- Desktop -> API: HTTP JSON to `http://<tally-server>:5107`, the address saved on the workstation in Settings → Server
- API -> PostgreSQL: direct DB connection (Npgsql)
- API -> TallyPrime: localhost HTTP/XML for health, reads and operator-initiated voucher writes
- ServerTray -> API: localhost HTTP JSON for health and DB maintenance

Desktop never talks to Tally directly.

---

## 2. Desktop app deployment

### Packaging

`tools\publish-prod.ps1` publishes the Desktop as a self-contained, single-file `TallyWrapper.exe` in `publish\prod\`. It carries no API and no database settings. There is no installer and no in-app updater: copy the contents of `publish\prod\` to the workstation, set the server address, and replace the files to upgrade.

### Server address

The Desktop's one connection setting is `DesktopBootstrap:ApiBaseUrl`, the address of the server API. The built-in value is `http://localhost:5107`, which is right for a Desktop on the server PC itself. Every other workstation saves its own in **Settings → Server**:

- **Find server** scans the workstation's own `/24` networks for a Tally Wrapper API on port `5107` and fills in what it finds
- **Test server** calls the server's `/api/health/live` and `/api/health`
- **Save and restart Billing** writes the address to `%APPDATA%\ShowroomBilling\desktop-bootstrap.local.json` and restarts the Desktop, which reads it at start-up

The environment variable `SHOWROOM_DESKTOP_DesktopBootstrap__ApiBaseUrl` overrides the saved address.

The file keeps the shape older builds wrote: `serverApiBaseUrl`, and a `connectionMode` that is always `Server`. A counter that an older build had already pointed at the server needs nothing done. A counter that has no saved address looks for the server on its own PC and shows the limited-mode screen until an address is saved; **Server Settings** on that screen opens the section.

The Desktop sends the device token only when its server address is loopback. A workstation on the LAN sends none; the server trusts its configured LAN range instead (§3).

The database also carries its own identity marker in `public.database_identity` (`key = 'environment'`). Production databases must be marked `PROD`; development/test databases must be marked `DEV`. Runtime health compares this DB-owned value with the API environment and surfaces a `DB MISMATCH` warning on mismatch while still reporting PostgreSQL as reachable.

### Local desktop persistence allowed

- the server address
- the admin unlock token, in memory only (short TTL)
- local UX preferences such as printer name and last PDF directory
- logs

No local durable business cache is allowed.

---

## 3. API deployment

### Recommended shape

- ASP.NET Core modular monolith
- **runs on the same machine as TallyPrime** (single-showroom deployment) — this is a hard topology constraint because the API dials Tally's localhost XML endpoint directly
- runs as a Windows Service on the Tally server, installed and repaired by `TallyWrapper.Server.exe`; nothing else starts it
- at start-up `DatabaseInitializationHostedService` applies pending EF migrations when `Database:AutoMigrateOnStartup` is true and its database role may alter the schema. On production the service's role may not, so migrations are applied by hand (§11)

### Operational requirements

- plain-text rolling log files, one line per event ([DEV_SETUP.md](../DEV_SETUP.md#logs) has the line shape): `C:\ProgramData\ShowroomBilling\logs` for the server service
- `/api/health/live` and `/api/health` endpoints
- `StuckPostingRecoveryHostedService` sweeps at start-up and every 5 minutes — any bill stranded in `posting` (the API stopped mid-push, or lost the database before it could record the outcome) moves to `reconciliation_required` with a `bill.posting.recovered` audit event once its claim is older than 10 minutes
- each successful admin unlock deletes the admin sessions that expired more than 30 days ago; nothing else is purged
- API errors are returned as RFC 7807 `application/problem+json` via a global `IExceptionHandler`, with no typed-body exceptions. See [`09_api_spec.md`](./09_api_spec.md) §1.
- API admin endpoints are gated via ASP.NET Core authentication/authorization — scheme `AdminToken` + policy `Admin`. No framework-wide authentication is required for non-admin routes.
- Safe `ITallyXmlClient` reads have a Polly retry pipeline (2 attempts, ~200 ms jittered backoff, triggered on `HttpRequestException` + 5xx). Voucher writes are excluded to prevent duplicate Tally imports; their total timeout still comes from `Connection.TimeoutSeconds`.

### Server install

The server is published with:

```powershell
.\tools\publish-server-tray.ps1
```

Copy `publish\server\TallyWrapper.Server.exe` to the Tally server and run it. This section is the one place the install is described; README and DEV_SETUP link here. The first run prompts for the trusted LAN CIDR (the box starts with the server PC's own network, read from its LAN adapter; a repair starts with the range already installed), elevates via UAC (answering No there installs nothing and is reported as "Setup did not finish"), extracts the API it carries to `C:\ProgramData\ShowroomBilling\bin`, installs the Tally Wrapper API Windows Service, registers the tray EXE under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, starts the service, and leaves the tray running. Setup reports once, when it has finished: "The server is installed and running." only after checking that the service is running; otherwise that it is installed but its service is stopped, that setup did not finish, or that it failed, with the end of `server-install.log`.

After the install, on the server PC, set the database connection in the tray dashboard (§5), then **Restart** on the Windows service line. Then point each workstation at the server (§2, Server address).

Running the same EXE again is idempotent. It checks the extracted API, service registration, service environment, firewall rule, maintenance token, tray startup registration, and service status, then repairs only missing or stale pieces.

When the EXE carries a different API from the installed one (an upgrade), the service is stopped, the new `ShowroomBilling.Api.exe` is written beside the old one and swapped in, and the one it replaced is kept in the same folder as `ShowroomBilling.Api.exe.previous`. The installed API is never written over in place, so a copy that fails part-way leaves it as it was. If any step of setup fails on a server that was already installed, the service is started again with whichever API is in place, and the failure is in `server-install.log`. To go back by hand: stop the service, rename `.previous` over the exe, start the service. A database that the newer build's migrations have already changed may not suit the older API (§11).

This EXE is the only server installer. The tray has no uninstall. To remove the server, exit the tray and run `tools\uninstall-server-api-service.ps1` elevated, as the user whose tray starts at sign-in: it removes the service, both firewall rule names and the start-up entry, and keeps `C:\ProgramData\ShowroomBilling` unless `-RemoveConfig` is given.

The service install sets:

- `ASPNETCORE_URLS=http://0.0.0.0:5107`
- `SHOWROOM_BILLING_SERVICE_NAME=<ServiceName>`
- `SHOWROOM_BILLING_APPDATA=C:\ProgramData\ShowroomBilling`
- `Logging__File__Directory=C:\ProgramData\ShowroomBilling\logs`
- `Database__AutoMigrateOnStartup=true`
- `DeviceAuth__Mode=TrustedLan`
- `DeviceAuth__TrustedNetworks__0=<LanCidr>`

It also creates `C:\ProgramData\ShowroomBilling\maintenance_token.txt` and a Windows Firewall rule scoped to the configured LAN CIDR. The range must be a network in CIDR form; it is checked before anything is written. A later run that only upgrades the API (a newer `TallyWrapper.Server.exe`) keeps the range already configured on the service and does not ask again. **Repair installation…** in the dashboard or the tray menu does ask, starting from the current range. The one-EXE installer uses the default LocalSystem service identity; DB configuration is written through the tray's localhost maintenance flow under `C:\ProgramData\ShowroomBilling`.

The server intentionally trusts clients inside the configured LAN CIDR for normal writes. Admin actions still require `X-Admin-Token`. The database maintenance endpoints and the initial admin-passcode setup are accepted from the server PC only, so the first admin passcode is set there (for example from a Desktop running on the server PC).

---

## 4. PostgreSQL deployment

Provision PostgreSQL 18 on a host reachable from the API. Give the API a runtime role with the data privileges it needs on the migrated tables and sequences. Use a separate owner credential to apply migrations. Keep the database hostname, role names, TLS trust configuration, credentials and rotation procedure in the site's private operations runbook.

The tray expects an Npgsql keyword connection string. Fill in the site's runtime values and TLS mode in this template.

```text
Host=<database-host>;Port=<port>;Database=<database>;Username=<runtime-role>;Password=<password>;SSL Mode=<tls-mode>
```

1. Apply the release migrations as the owner. Set the database identity to `PROD` for production or `DEV` for development. The [database setup](../DEV_SETUP.md#database) includes the marker statement.
2. On the API server, open the tray dashboard, enter the runtime connection string, test it, save it and restart the API service. Configure TLS and certificate trust according to the database endpoint. The tray saves the connection locally with DPAPI protection.
3. Confirm the Database indicator reports the expected identity and no pending migrations. For credential rotation, test and save the replacement in the tray and verify the service after restart before retiring the old credential where supported.

The database operator owns patching, certificate renewal, disk capacity and automated backups. Check the PostgreSQL version and checksum settings after an upgrade. Test restores regularly and configure point-in-time recovery where available. Take a dump before applying migrations and rehearse restores on an isolated database. See the [upgrade procedure](#11-upgrade-flow). Development and automated tests can use throwaway containers. See [database tests](../DEV_SETUP.md#database-tests).

## 5. Server tray

`ShowroomBilling.ServerTray` is installed only on the Tally server and started at user login. It is a companion UI and bootstrapper, not the long-running API host. Only one tray runs per signed-in user: starting the exe again asks the one that is running to close, takes its place and opens the dashboard. It uses Windows Service Control Manager for service state and control, so start/stop/restart does not depend on the API answering. Windows lets only an administrator start or stop a service, and the tray runs with the signed-in user's ordinary rights: when Windows refuses, **Start**, **Restart**, **Stop** and **Stop server and exit** do the action in an elevated copy of the tray, behind the same Windows permission prompt as Install. Answering No there leaves the service as it was. The window stays usable while the service starts or stops; the three buttons are off until it has.

The dashboard (double-click the tray icon, or **Open dashboard** in its menu) opens on one sentence: **Ready for billing**, or **Counters cannot bill** and why. Under it:

- three health lines, each with a dot, a sentence and a chip: **Windows service** (with **Start** when it is stopped, **Restart** and **Stop** when it is running, **Install** when it is missing), **Server** (whether the API answers on this PC) and **Database** (answering, not answering, upgrade needed, or marked as another database)
- **Address for the counters**, with **Copy**: what goes into Settings → Server on each counter
- **Counters connected**: the workstations heard from in the last 2 minutes (PC and user, address, last seen)
- **Database connection**: paste a new connection string, **Test**, then **Save**. This goes through localhost-only maintenance endpoints using `maintenance_token.txt`; it is the only place the connection is changed, and no screen shows the stored one. A saved connection is used once the service has been restarted
- in the grey foot: **Logs**, **Data folder**, **Install log**, **Repair installation…**, and on the right **Stop server and exit**

The tray menu offers the same, plus **Open health page**. Every message is a dialog whose text can be copied (**Copy**), so an error can be sent on as text. There is no Tally polling; Tally is checked only by operator-triggered push/refresh/System Health flows.

**Stop server and exit** asks first, then stops `ShowroomBilling.Api` through Windows Service Control Manager and closes the tray. This is the server shutdown path for the Tally host. (Builds up to 11 October 2026 called it **Exit Tray**, and the buttons above **Install / Repair Server**, **Start / Stop / Restart API Service**, **Test DB** and **Save DB**.)

The look is the Desktop's Ledger Clear, drawn in WinForms: see [15_ui_design_reference.md §12](15_ui_design_reference.md).

---

## 6. Start-up tasks

- `DatabaseInitializationHostedService` — applies pending EF migrations when auto-migrate is on, and records how many are pending. A bounded start-up task.
- `StuckPostingRecoveryHostedService` — moves abandoned `posting` bills to `reconciliation_required`. It sweeps once the database initialiser has finished and then every 5 minutes for as long as the service runs, also when the database was not reachable at start-up. A sweep is one small read (plus one update per abandoned bill); it holds off while the database is behind its migrations, and a sweep that fails is logged and followed by the next.

The API’s recovery sweep reads the database and updates abandoned claims. Periodic Desktop health requests can query Tally. Each voucher write requires an operator action.

---

## 7. Tally host assumptions

- the API is the only component that speaks Tally XML; the supported topology is the API on the Tally host
- TallyPrime is reachable on `http://{host}:{port}` from the API process (typically `127.0.0.1:9000`)
- host and port are configured via cloud settings `Connection.Host` / `Connection.Port`
- selected company exists in Tally and is open/available
- firewall rules permit API -> TallyPrime traffic

---

## 8. Logs and diagnostics

### Desktop logs

Should capture:

- startup status, including one `[startup-timing] hostBuild=… hostStart=… resolveWindow=… showWindow=… windowVisibleAt=…` line per launch
- API connectivity failures
- print failures
- admin/recovery actions where appropriate

Location: `%APPDATA%\ShowroomBilling\logs` on each workstation (rolling files, `desktop-*.log`).

### API logs

Should capture:

- request traces with correlation IDs
- bill finalization failures
- numbering allocation failures
- state transitions (bill.* audit events are also on disk via DB)
- Tally call outcomes (`tally.posted`, `tally.failed`, `tally.outcome.unknown`, `TALLY_TRANSPORT_UNKNOWN`, `TALLY_TIMEOUT`, etc.) and safe-read retry attempts emitted by `Microsoft.Extensions.Http.Resilience`
- admin and recovery actions
- `StuckPostingRecoveryHostedService` recoveries and failed sweeps
- one line when the database stops answering (`The database is not answering.`) and one when it answers again; the health checks in between write nothing

Location: `C:\ProgramData\ShowroomBilling\logs` on the server (`api-*.log`). An API started by hand in development writes to its own data folder: `%APPDATA%\ShowroomBilling\logs`, or the scratch folder under `tools\run-scratch.ps1`.

---

## 9. Health checks

### API endpoints

- `/api/health/live` — liveness
- `/api/health` — the database, from a check of PostgreSQL (reachable, identity, pending migrations) that is at most 5 seconds old, and the freshness of the companies/ledgers/stock-items/voucher-types lists
- `/api/health/tally-company` — Tally reachability + active-company check
- `/api/clients/presence` — localhost-only in-memory list of workstations seen in the last 2 minutes

The Desktop and server tray poll `/api/health` every 15 seconds. The API shares a database check for up to five seconds. The Desktop checks Tally company status on startup, on a System Health refresh and every 30 minutes. Monitor database availability, storage and resource use on the database host.

On the first report of an unreachable database, the Desktop turns the Database indicator red. A second consecutive routine failure pauses billing. Startup and explicit refresh act on the first result. Billing resumes after the next successful check. The tray distinguishes an unreachable database, pending migrations, an identity mismatch and an unavailable API.

### Tally reachability

The API checks the configured company before a push, retry or repost. If Tally is unreachable or the company is closed, preflight returns `503 Tally unavailable` and preserves the bill state. System Health also requests this check independently.

If Tally becomes unavailable after preflight, a timeout/transport/unreadable-response outcome settles the bill in `reconciliation_required`; an explicit Tally business rejection settles it in `failed`. Each operator action makes at most one voucher write.

### Master freshness

- `/api/health` surfaces the age of each master snapshot
- operators trigger a refresh via "Refresh from Tally" in Settings or the System Health dialog
- no automatic freshness-based warning; freshness is informational

---

## 10. Restart and recovery procedures

### Desktop restart

- safe; no business truth is lost because bills live only in API/DB
- on reconnect, desktop reloads runtime and bill states

### API restart

- safe
- `StuckPostingRecoveryHostedService` moves any bill stranded in `posting` to `reconciliation_required`; the operator verifies Tally and chooses admin Mark as Pushed or Mark as Pending. A restart is not needed for this: the same sweep runs every 5 minutes while the service is up
- data is in PostgreSQL; nothing lost

### Tally outage recovery

- bills continue to be saved (numbering, saving, audit all live in the API + DB)
- preflight blocks Push with `503 Tally unavailable` while Tally is down and leaves the bill unchanged
- if connectivity is lost after a voucher request begins, the bill requires reconciliation rather than an automatic retry

---

## 11. Upgrade flow

### API / DB

- deploy API compatible with next DB migration
- **On production, nothing applies migrations by itself. Apply them by hand, as the role that owns the tables, before a new server or Desktop build is started.**
  - The server service does try at start-up (the tray installer sets `Database__AutoMigrateOnStartup=true`), but it connects as the restricted application role, which may not create or alter objects. As soon as a migration is pending its attempt is refused with SQLSTATE `42501`, logged as a warning, and the API carries on. A release with nothing pending never reaches that code, which is how this stayed hidden until 10 October 2026.
  - The Desktop has no API and migrates nothing.
  - A build running against a database that has not had its migrations cannot read or save a bill, although PostgreSQL is reachable and settings and masters load. It says so in four places: bill and numbering requests answer `503 Database upgrade needed`; `/api/health` reports `pendingMigrationCount` (see [09_api_spec.md](09_api_spec.md)); the Desktop shows `DB BEHIND`, a banner, and the reason above the Bills list; the server tray's database line reads "N migrations of this version have not been applied. Bills cannot be read or saved until the database owner applies them." with the chip `Upgrade needed`. Before 10 October 2026 none of this existed: those requests were a bare `500`, health read Healthy and the Bills tab was simply empty. The detailed reason for a refused start-up migration is in the server's log (`C:\ProgramData\ShowroomBilling\logs\api-*.log`, search for `migration`).
  - To apply them: first look at what the database has (`SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 3`); do not assume. Then either generate a script with `dotnet ef migrations script <last applied migration> --idempotent --output <file>` (no database connection needed) and run it with `psql -v ON_ERROR_STOP=1` as the owner role where the database is reachable, or run `dotnet ef database update --connection ...` with the owner role's connection string. The API service does not have to be restarted afterwards: the next health check, seconds later, finds nothing pending, the banner goes, and the stuck-posting sweeps, which hold off while the database is behind, resume. (Restarting it does no harm.)
  - A development or test database whose application role owns its tables does migrate itself; that is why this does not show up there.
- preserve backward-compatibility window if desktop rollout is staged

**A release that drops columns or tables cannot be staged.** An older build still maps what was dropped, so once the migrations are applied to the shared database, every older API pointed at it fails on its next bill read or write ("column does not exist"). That covers an older server service, and any counter still on an old Desktop build that runs its own embedded API. For such a release: close Billing on every counter and stop the server service, take the dump below, apply the migrations by hand as the owner role (see above), update every machine, then start them. The release of 10 October 2026 is one (`DropUnusedColumnsAndIndexes`, `DropDraftEditLeases`). Migrations that only add things do not have this limit.

`DropInvoiceNumberReservations` (the release after 11 October 2026) is a narrower case. The table it drops was last written by the build of 10 October; the build of 11 October (`627ef1f`) still maps it but never touches it. So the migration can be applied while a server from 11 October is running, and the new server started afterwards. A server exe from 10 October or earlier cannot save a bill against the migrated database; to go back that far, run the migration's `Down` first, which restores the table and its rows.

The release after 14 November 2026 carries three migrations:

- `UniqueInvoiceCore` only changes an index (the number core becomes unique per fiscal year), so it does not tie the server to the database. On a database that already holds two bills of one year on one core it keeps the plain index and raises a `WARNING`; [10_database_schema.md §1.1](10_database_schema.md) has the query that finds them and the two statements that make the index unique afterwards.
- `DropUnreadColumns` drops eleven columns, so **the server and the database change together**: stop the server service, take the dump below, apply the migrations as the owner role, install the new `TallyWrapper.Server.exe`, start the service. A server exe older than this release still maps those columns and cannot read or save a bill on the migrated database; to go back to one, run the migration's `Down` first, which restores the columns and their values from the `dropped_20261114_*` tables. The counters are not tied to it: a Desktop reads none of the removed fields, so counters can be updated before or after the server.
- `MasterSnapshotsInOneTable` replaces the five tables of Tally lists with one, and is tied to the server in the same way and in the same step: an older server exe finds no lists and cannot refresh them on the migrated database. The lists are carried across, so nothing has to be refreshed from Tally afterwards. Its `Down` puts the five tables back from the `dropped_20261114_*_rows` copies. Run the `Down`s newest first when going back.

The release after 15 November 2026 carries one more, `DropShowroomAndDocumentType`: it drops `ShowroomId` from five tables and `DocumentType` from `invoice_sequences`, whose key becomes the fiscal year ([10_database_schema.md §1.10](10_database_schema.md)). **The server and the database change together**, as for `DropUnreadColumns` and by the same steps: stop the server service, take the dump below, apply the migration as the owner role, install the new `TallyWrapper.Server.exe`, start the service. A server exe older than this release still writes those columns and cannot save or list a bill on the migrated database; to go back to one, run the migration's `Down` first. The counters are not tied to it: the two fields that left the API's answers (`documentType` of a number preview, `showroomId` of a print asset) were read by no Desktop.

That release also moved health to one address, `GET /api/health`, and its server still answered the three addresses an older Desktop asks, so the counters could be updated before or after it. **The server release after it no longer answers those three.** Every counter must run a Desktop from the release of 17 November 2026 or later before that server is installed: an older one finds the server but shows `DB ?` and never learns that the database is down or behind. (The other way round is the same: a current Desktop pointed at a server older than 17 November shows `DB ?`.)

The release after 21 November 2026 carries one migration, `AddBatchHours`: it adds the column `BatchHoursJson` (text, default `{}`) to `cloud_settings`, for the shop hours a batch of the Batch Data Scheduler keeps to ([10_database_schema.md §1.7](10_database_schema.md), [17_synthetic_batch.md](17_synthetic_batch.md#shop-hours)). **Apply it by hand, as the owner role, before the new server build is started**, as above: a new server on a database without the column cannot read the settings row, and reports the database as behind. It only adds, so the server and the database need not change together: a server exe older than this release does not name the column and runs on the migrated database as before, and the column's default fills it for any row that build writes. The migration can therefore be applied while the old server is running, and the new one installed afterwards; nothing has to be stopped for it. The counters are not tied to it either, though the batch scheduler's hours appear only on a Desktop of this release. Its `Down` drops the column and the hours saved in it, which are then set again in Settings → Admin; no older build needs the `Down` run.

That release also changes how the first row of `cloud_settings` is made, so that requests arriving together on a new database add one row and not one each. It has no migration and an existing database needs nothing done. A database first used by an older build may hold a second, unused settings row; [10_database_schema.md §1.7](10_database_schema.md) has the query that shows it and the statement that removes it.

The release after 27 November 2026 brings **bank charges** on a bill (a typed amount added after GST, credited to its own Tally ledger) and carries one migration, `AddBankChargesLedger`: it adds the column `BankChargesLedger` (varchar 256, default `''`) to `cloud_settings` ([10_database_schema.md §1.7](10_database_schema.md)). It only adds, like `AddBatchHours`: **apply it by hand, as the owner role, before the new server build is started** (a new server on a database without the column cannot read the settings row); an older server runs on the migrated database as before, so it can be applied while the old server is running. Its `Down` drops the column and the ledger chosen in it. The order of the rest matters, because a bill's bank charges are new data the older builds do not know:

1. **The server first.** An older server refuses a bill with bank charges (its balance check does not know them), so nothing is stored wrong, but no counter can save one until the server is new.
2. **Every counter before the first bill with bank charges.** An older Desktop reads such a bill without them: Bill Details and a reprint fold them into the round-off, and an edit saved from it drops them from the bill. A newer Desktop against an older server keeps working for bills without bank charges.
3. **Choose the ledger in Settings → Ledgers** ("Bank charges ledger") before the first bill with bank charges is pushed. Until one is chosen, such a push is refused before anything is sent (`CONFIG_MISSING_BANK_CHARGES_LEDGER`); bills without bank charges never need it. An older counter's settings save leaves the chosen ledger as it is.

Take a database dump **before applying migrations**. From a private backup directory, use a PostgreSQL 18 client with `PGHOST`, `PGPORT`, `PGDATABASE` and `PGUSER` set for the intended database and a password source configured for that client.

```powershell
$dump = "billing_$(Get-Date -Format yyyyMMdd-HHmmss).dump"
pg_dump --format=custom --no-owner --file $dump
if ($LASTEXITCODE -ne 0) { throw 'Database backup failed.' }
pg_restore --list $dump
if ($LASTEXITCODE -ne 0) { throw 'Could not read the backup contents.' }
```

Listing the archive checks its table of contents. To verify that it restores and migrates, use an isolated [scratch run with `-RestoreFrom`](../DEV_SETUP.md#run-against-a-scratch-database).

A migration that removes data must also protect itself, because that step can be forgotten: it copies what it is about to drop into a side table in the same transaction, and its `Down` restores from that copy. `DropUnusedColumnsAndIndexes` (10 October 2026) is the model, and `DropUnreadColumns` (14 November 2026) and `DropShowroomAndDocumentType` (15 November 2026) the same with one copy per table; see [10_database_schema.md §1.10](10_database_schema.md) for the tables they leave and how to remove them.

### Desktop

- to upgrade a counter, replace its files with the new `publish\prod\` output (§2)
- a staged counter rollout is acceptable while contracts remain compatible and the release drops nothing from the schema
- nothing checks that a Desktop and the API it calls are the same build; when a contract changes, update the server and every counter together
- a counter coming from a build that ran its own embedded API opens on the limited-mode screen until a server address is saved in Settings → Server. An address that build had saved without using it is not taken up. Delete that older build's leftovers when the counter is upgraded: the folder `%LOCALAPPDATA%\ShowroomBilling\runtime\api` (about 130 MB) and the file `%APPDATA%\ShowroomBilling\database.*.local.json`, which holds that workstation's old database password and is no longer read by anything

---

## 12. Operational caveats

### Online-first consequence

This architecture is intentionally online-first. If the API is unavailable, the desktop cannot save bills. There is one API for the whole showroom: while the server PC is off or its service is stopped (the tray's **Stop server and exit** stops it), every counter shows the limited-mode screen. Reasoning:

- no local durable business storage on the desktop
- invoice numbers are reserved at save time by the API's numbering service

### Manual-only Tally consequence

If Tally is down while the API and database are available, operators can save bills as `pending`. After a preflight failure they can push again once Tally is back. An uncertain write outcome requires checking the voucher in Tally before resolving the local bill.

### Other caveats

- Tally XML remains a brittle integration boundary
- printer driver differences still exist even with better document generation
- a long push takes the full duration of Tally's response (5–30 seconds is normal); the operator's Push button stays disabled for that interval
- if the business later requires full offline billing continuity OR automatic retry, the architecture must be revisited explicitly
