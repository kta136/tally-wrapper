# Deployment and Operations

This document describes how Tally Wrapper is deployed and operated.

---

## 1. Deployment topology

### Components

- WPF desktop app on each counter workstation. It is a client of the server API and never starts an API of its own
- one ASP.NET Core API, a Windows Service **on the same machine as TallyPrime** (reaches Tally's XML endpoint over localhost)
- `ShowroomBilling.ServerTray` on the Tally server: the installer for that service, and its companion in the logged-in user's taskbar notification area
- PostgreSQL database (PostgreSQL 18 on an Oracle Cloud Infrastructure VPS in production; development uses a throwaway PostgreSQL 18 container, see [DEV_SETUP.md](../DEV_SETUP.md#run-against-a-scratch-database))
- TallyPrime running on the Tally host

No separate bridge process. No SignalR hub. No job queue.

### Communication paths

- Desktop -> API: HTTP JSON to `http://<tally-server>:5107`, the address saved on the workstation in Settings → Server
- API -> PostgreSQL: direct DB connection (Npgsql)
- API -> TallyPrime: localhost HTTP/XML (synchronous, only on operator click)
- ServerTray -> API: localhost HTTP JSON for health and DB maintenance

Desktop never talks to Tally directly.

---

## 2. Desktop app deployment

### Packaging

`tools\publish-prod.ps1` publishes the Desktop as a self-contained, single-file `TallyWrapper.exe` in `publish\prod\`. It carries no API and no database settings. There is no installer and no in-app updater: copy the contents of `publish\prod\` to the workstation, set the server address, and replace the files to upgrade.

### Server address

The Desktop's one connection setting is `DesktopBootstrap:ApiBaseUrl`, the address of the server API. The built-in value is `http://localhost:5107`, which is right for a Desktop on the server PC itself. Every other workstation saves its own in **Settings → Server**:

- **Find server** scans the workstation's own `/24` networks for a Tally Wrapper API on port `5107` and fills in what it finds
- **Test server** calls the server's `/api/health/live` and `/api/health/ready`
- **Save and restart Billing** writes the address to `%APPDATA%\ShowroomBilling\desktop-bootstrap.local.json` and restarts the Desktop, which reads it at start-up

The environment variable `SHOWROOM_DESKTOP_DesktopBootstrap__ApiBaseUrl` overrides the saved address.

The file keeps the shape older builds wrote: `serverApiBaseUrl`, and a `connectionMode` that is always `Server`. A counter that an older build had already pointed at the server needs nothing done. A counter that has no saved address looks for the server on its own PC and shows the limited-mode screen until an address is saved; **Server Settings** on that screen opens the section.

The Desktop sends the device token only when its server address is loopback. A workstation on the LAN sends none; the server trusts its configured LAN range instead (§3).

The database also carries its own identity marker in `public.database_identity` (`key = 'environment'`). Production databases must be marked `PROD`; development/test databases must be marked `DEV`. Runtime health compares this DB-owned value with the API environment and surfaces a `DB MISMATCH` warning on mismatch while still reporting PostgreSQL as reachable.

### Local desktop persistence allowed

- the server address and the counter name
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

- structured logging to rolling log files: `C:\ProgramData\ShowroomBilling\logs` for the server service
- `/api/health/live` and `/api/health/ready` endpoints
- `StuckPostingRecoveryHostedService` runs once on boot — any bill stranded in `posting` from a prior crash moves to `reconciliation_required` with a `bill.posting.recovered` audit event
- each successful admin unlock deletes the admin sessions that expired more than 30 days ago; nothing else is purged
- API errors are returned as RFC 7807 `application/problem+json` via a global `IExceptionHandler`, with no typed-body exceptions. See [`09_api_spec.md`](./09_api_spec.md) §1.
- API admin endpoints are gated via ASP.NET Core authentication/authorization — scheme `AdminToken` + policy `Admin`. No framework-wide authentication is required for non-admin routes.
- Safe `ITallyXmlClient` reads have a Polly retry pipeline (2 attempts, ~200 ms jittered backoff, triggered on `HttpRequestException` + 5xx). Voucher writes are excluded to prevent duplicate Tally imports; their total timeout still comes from `Connection.TimeoutSeconds`.

### Server install

The server is published with:

```powershell
.\tools\publish-server-tray.ps1
```

Copy `publish\server\TallyWrapper.Server.exe` to the Tally server and run it. The first run prompts for the trusted LAN CIDR, elevates via UAC, extracts the API it carries to `C:\ProgramData\ShowroomBilling\bin`, installs the Tally Wrapper API Windows Service, registers the tray EXE under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, starts the service, and leaves the tray running.

Running the same EXE again is idempotent. It checks the extracted API, service registration, service environment, firewall rule, maintenance token, tray startup registration, and service status, then repairs only missing or stale pieces.

This EXE is the only server installer. The tray has no uninstall. To remove the server, exit the tray and run `tools\uninstall-server-api-service.ps1` elevated, as the user whose tray starts at sign-in: it removes the service, both firewall rule names and the start-up entry, and keeps `C:\ProgramData\ShowroomBilling` unless `-RemoveConfig` is given.

The service install sets:

- `ASPNETCORE_URLS=http://0.0.0.0:5107`
- `SHOWROOM_BILLING_SERVICE_NAME=<ServiceName>`
- `SHOWROOM_BILLING_APPDATA=C:\ProgramData\ShowroomBilling`
- `Logging__File__Directory=C:\ProgramData\ShowroomBilling\logs`
- `Database__AutoMigrateOnStartup=true`
- `DeviceAuth__Mode=TrustedLan`
- `DeviceAuth__TrustedNetworks__0=<LanCidr>`

It also creates `C:\ProgramData\ShowroomBilling\maintenance_token.txt` and a Windows Firewall rule scoped to the configured LAN CIDR. The range must be a network in CIDR form; it is checked before anything is written. A later run that only upgrades the API (a newer `TallyWrapper.Server.exe`) keeps the range already configured on the service and does not ask again. **Install / Repair Server** in the dashboard does ask, starting from the current range. The one-EXE installer uses the default LocalSystem service identity; DB configuration is written through the tray's localhost maintenance flow under `C:\ProgramData\ShowroomBilling`.

The server intentionally trusts clients inside the configured LAN CIDR for normal writes. Admin actions still require `X-Admin-Token`. The database maintenance endpoints and the initial admin-passcode setup are accepted from the server PC only, so the first admin passcode is set there (for example from a Desktop running on the server PC).

---

## 4. PostgreSQL deployment

### Production host

Production uses PostgreSQL 18 self-hosted on an Oracle Cloud Infrastructure VPS. It is not a managed database service: the team owns the operating system, PostgreSQL installation and upgrades, backups, restore testing, certificates, monitoring, and storage capacity.

Configure the API or server tray with an Npgsql connection string shaped like:

```text
Host=<oracle-vps-host>;Port=5432;Database=tally_wrapper_prod;Username=<app-role>;Password=<secret>;SSL Mode=<vps-required-mode>;Minimum Pool Size=<min>;Maximum Pool Size=<max>
```

Only this keyword form is accepted. A `postgres://` or `postgresql://` URL is refused with a message that names the form above.

### OpenBao production secret

Canonical shared-environment database secrets are stored in the OpenBao KV v2 mount `kv`:

- production: `Postgres/apps/tally_wrapper/prod`; structured fields are authoritative and `connection_string` is synchronized from them
- persistent test: `Postgres/apps/tally_wrapper_test/dev`, key `connection_string`

The production connection fields are `host`, `port`, `database`, `username`, `password`, `ssl_mode`, `minimum_pool_size`, and `maximum_pool_size`. Other keys in the same secret are operational metadata and must be preserved. `connection_string` is a derived convenience value for Npgsql consumers; it is not an independent credential. When any authoritative connection field changes, regenerate the derived value during the same controlled rotation and verify both representations before deploying it.

The API and server tray do not call OpenBao directly. An authorized operator retrieves the value with the native `bao` CLI, then injects it into the server tray, the DPAPI-protected server override, or the API's `SHOWROOM_BILLING_POSTGRES` environment variable. Use the native CLI for reads and rotations so version checks and field-preserving patches are explicit and reproducible; do not make the application depend on an interactive browser or Bitwarden session.

Retrieve the value without printing it:

```powershell
$secretPath = 'Postgres/apps/tally_wrapper/prod'
$connection = bao kv get -mount=kv -field=connection_string $secretPath
```

To synchronize `connection_string` from the current production fields, use a CAS-protected patch. `DbConnectionStringBuilder` quotes special characters correctly. `ProcessStartInfo.StandardInput.Write` sends the value without placing it in shell history, process arguments, or adding a trailing line ending:

```powershell
$secret = bao kv get -mount=kv -format=json $secretPath | ConvertFrom-Json
$data = $secret.data.data
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder['Host'] = [string]$data.host
$builder['Port'] = [int]$data.port
$builder['Database'] = [string]$data.database
$builder['Username'] = [string]$data.username
$builder['Password'] = [string]$data.password
$builder['SSL Mode'] = [string]$data.ssl_mode
$builder['Minimum Pool Size'] = [int]$data.minimum_pool_size
$builder['Maximum Pool Size'] = [int]$data.maximum_pool_size
$connection = $builder.ConnectionString

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Get-Command bao).Source
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
@('kv', 'patch', '-mount=kv', "-cas=$($secret.data.metadata.version)", $secretPath, 'connection_string=-') |
    ForEach-Object { [void]$startInfo.ArgumentList.Add($_) }
$process = [System.Diagnostics.Process]::Start($startInfo)
$process.StandardInput.Write($connection)
$process.StandardInput.Close()
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "OpenBao patch failed with exit code $($process.ExitCode)." }
Remove-Variable connection, builder, data, secret
```

If a password, host, port, database, username, TLS mode, or pool limit is being rotated, update that structured field and the newly built `connection_string` together as one reviewed KV v2 change. Do not use `bao kv put` for this path: it replaces the secret data and can destroy the other fields. Never echo the retrieved connection, save it in the repository, or paste it into issue/CI logs.

Use the TLS mode and certificate settings configured on the VPS. Limit inbound TCP `5432` to the API host or its trusted network in both the Oracle Cloud network rules and the VPS firewall; do not expose PostgreSQL to unrestricted internet traffic. Pass the retrieved connection explicitly when applying migrations:

```powershell
dotnet ef database update --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --connection $connection
Remove-Variable connection
```

### Shared persistent test database

The Coolify `shared-postgres` stack owns both PostgreSQL 18 and PgBouncer. `tally_wrapper_test` is a persistent application test database inside that existing cluster, not a separate Coolify PostgreSQL resource.

| Component | Contract |
|---|---|
| Database | `tally_wrapper_test`, owned by `tally_wrapper_test_dev_owner` |
| Runtime role | `tally_wrapper_test_dev_api`; `LOGIN`, `NOINHERIT`, no create-role/database privilege, connection limit `5` |
| Migrator role | `tally_wrapper_test_dev_migrator`; member of the owner role, normally `NOLOGIN` with no verifier |
| PgBouncer mapping | `tally_wrapper_test` -> `postgres:5432/tally_wrapper_test`, pool size `2`, reserve `0`, max backend connections `2` |
| Runtime route | `pgbouncer:6432` on external Docker network `shared-postgres-private` |
| Secret | `kv/Postgres/apps/tally_wrapper_test/dev`; versioned KV v2 data with `connection_string` plus structured connection fields |
| Environment marker | `public.database_identity.environment = 'DEV'` |

The PgBouncer pool runs in transaction mode. Coolify applications must join `shared-postgres-private` and use the PgBouncer hostname; PostgreSQL itself has no public host port and application roles must not bypass the pooler. Runtime access is limited to this database, and host validation checks that the test API role cannot connect to the production or development databases.

For migrations, enable the managed migrator login only for the bounded migration window, pass the test connection explicitly to `dotnet ef database update`, set the database identity to `DEV`, reconcile runtime grants, and immediately return the migrator to `NOLOGIN` with its verifier removed. Store the usable runtime credential only in OpenBao; do not leave a plaintext or decryptable provisioning copy on the database host.

This database supports a persistent test API deployment. It must not be used as `SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION`: the relational fixture connects to the `postgres` admin database and creates/drops isolated `tw_test_<guid>` databases, which requires a separate restricted administration contract and a compatible routing path. The default Docker/CI test harness remains the supported path.

### Role

PostgreSQL is the system of record for:

- bills
- bill revisions
- numbering sequences
- audit events
- master snapshots (companies, ledgers, stock items, voucher types)
- admin passcodes and sessions
- print assets
- cloud settings

### Requirements

- PostgreSQL 18 in production
- automated backups
- point-in-time recovery if possible
- regular restore drills
- operating-system and PostgreSQL security patching
- TLS certificate renewal and expiry monitoring when TLS is enabled
- alerting on connection failures and storage pressure
- clear migration strategy for schema changes

### PostgreSQL 18 baseline verification

After provisioning or a major-version upgrade, verify the active production cluster from an administrative session:

```sql
SHOW server_version;
SHOW data_checksums;
SHOW io_method;
SHOW effective_io_concurrency;
SHOW maintenance_io_concurrency;
SHOW password_encryption;
```

Keep the server on the latest supported PostgreSQL 18 patch release. `data_checksums` should be enabled; PostgreSQL 18 enables checksums for newly initialized clusters, but an upgraded older cluster can retain its prior checksum setting. Enabling checksums later is an offline VPS maintenance operation and must be paired with a verified backup and restore plan.

Use `scram-sha-256` for the application role and matching `pg_hba.conf` rules. PostgreSQL 18 asynchronous I/O should normally report `io_method = worker`; benchmark `io_uring` against the Oracle VPS storage before selecting it. Do not raise I/O concurrency values without measuring query latency and storage saturation. Run `ANALYZE` after a major upgrade and use `EXPLAIN (ANALYZE, BUFFERS)` plus PostgreSQL statistics views before adding or removing indexes.

The EF provider is explicitly targeted at PostgreSQL 18. New application-created database entity IDs use UUIDv7, while stable MD5-derived identity GUIDs remain deterministic. Bill search uses concurrent GIN trigram indexes on invoice number and party name; apply their migration with the explicit production connection command shown above. The index migration runs outside a transaction and drops any same-named partial index before recreating it concurrently, so an interrupted pre-history run can be retried safely.

---

## 5. Server tray

`ShowroomBilling.ServerTray` is installed only on the Tally server and started at user login. It is a companion UI and bootstrapper, not the long-running API host. It uses Windows Service Control Manager for service state and control, so start/stop/restart still works when the API is stopped.

Tray features:

- show service state, API liveness, DB health, and recently seen workstation clients (counter, machine\user, address, last seen)
- install/repair the server, start/stop/restart the API Windows Service, open the local health page, open logs/config/install-log folders, and copy the workstation server URL from either the tray menu or the main dashboard window
- configure/test/save the server's database connection through localhost-only maintenance endpoints using `maintenance_token.txt`; this is the only place it is changed, and no Desktop screen shows it. DB maintenance result dialogs include a **Copy** action for supportable error messages
- exit the tray by stopping the API Windows Service first, then closing the companion UI
- no Tally polling; Tally is checked only by operator-triggered push/refresh/System Health flows

The tray's normal **Exit Tray** action stops `ShowroomBilling.Api` through Windows Service Control Manager before closing the companion tray UI. This is the server shutdown path for the Tally host.

---

## 6. Start-up tasks

- `DatabaseInitializationHostedService` — applies pending EF migrations when auto-migrate is on, and records how many are pending.
- `StuckPostingRecoveryHostedService` — moves abandoned `posting` bills to `reconciliation_required`.
- `SequenceSelfHealHostedService` — bounded repair of invoice sequences after database readiness.

These are bounded start-up tasks. There is no recurring posting, Tally polling, or retention worker after they finish.

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
- `StuckPostingRecoveryHostedService` startup report

Location: `C:\ProgramData\ShowroomBilling\logs` on the server (`api-*.log`). An API started by hand in development writes to its own data folder: `%APPDATA%\ShowroomBilling\logs`, or the scratch folder under `tools\run-scratch.ps1`.

---

## 9. Health checks

### API endpoints

- `/api/health/live` — liveness
- `/api/health/ready` — whether PostgreSQL answers (pending migrations are reported by `/api/runtime/health`)
- `/api/health/masters` — freshness of companies/ledgers/stock-items/voucher-types snapshots
- `/api/health/tally-company` — operator-triggered Tally reachability + active-company check
- `/api/runtime/health` — cheap runtime status by default; pass `?forceDatabase=true` for a PostgreSQL-backed DB/identity probe
- `/api/clients/presence` — localhost-only in-memory list of workstations seen in the last 2 minutes

Desktop background health polling uses cheap runtime probes so it does not add a database round trip on every tick. Full DB health, Tally-company health, and master freshness are requested on startup, explicit System Health refreshes, and a slower scheduled probe. The Oracle VPS PostgreSQL service remains an always-on infrastructure dependency, so availability, resource use, and storage pressure must be monitored at the VPS/database layer.

When the server reports that it cannot reach its database, the Desktop shows the limited-mode screen with the banner "The server cannot reach its database. On the server PC, open the Tally Wrapper tray and check the database connection." It does not open a settings screen by itself.

### Tally reachability

Tally reachability is only verified when the operator clicks Push/Retry/Repost, Push All Pending, Refresh from Tally, or Refresh in the System Health dialog. Push-family endpoints run a Tally company preflight before any bill moves to `posting`; if Tally is unreachable or the configured active company is not open, the API returns `503 Tally unavailable` and leaves bill state unchanged.

If Tally becomes unavailable after preflight, a timeout/transport/unreadable-response outcome settles the bill in `reconciliation_required`; an explicit Tally business rejection settles it in `failed`. The architecture still forbids background Tally polling and voucher-write retries.

### Master freshness

- `/api/health/masters` surfaces age of each master snapshot
- operators trigger a refresh via "Refresh from Tally" in Settings or the System Health dialog
- no automatic freshness-based warning; freshness is informational

---

## 10. Restart and recovery procedures

### Desktop restart

- safe; no business truth is lost because bills live only in API/DB
- on reconnect, desktop reloads runtime and bill states

### API restart

- safe
- `StuckPostingRecoveryHostedService` moves any bill stranded in `posting` to `reconciliation_required`; the operator verifies Tally and chooses admin Mark as Pushed or Mark as Pending
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
  - A build running against a database that has not had its migrations cannot read or save a bill, although PostgreSQL is reachable and settings and masters load. It says so in four places: bill and numbering requests answer `503 Database upgrade needed`; `/api/runtime/health` reports `pendingMigrationCount` (see [09_api_spec.md](09_api_spec.md)); the Desktop shows `DB BEHIND`, a banner, and the reason above the Bills list; the server tray's database line reads `upgrade needed: N migration(s) not applied`. Before 10 October 2026 none of this existed: those requests were a bare `500`, health read Healthy and the Bills tab was simply empty. The detailed reason for a refused start-up migration is in the server's log (`C:\ProgramData\ShowroomBilling\logs\api-*.log`, search for `migration`).
  - To apply them: first look at what the database has (`SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 3`); do not assume. Then either generate a script with `dotnet ef migrations script <last applied migration> --idempotent --output <file>` (no database connection needed) and run it with `psql -v ON_ERROR_STOP=1` as the owner role where the database is reachable, or run `dotnet ef database update --connection ...` with the owner role's connection string. Restart the API service afterwards so the start-up tasks that stepped aside run.
  - A development or test database whose application role owns its tables does migrate itself; that is why this does not show up there.
- preserve backward-compatibility window if desktop rollout is staged

**A release that drops columns or tables cannot be staged.** An older build still maps what was dropped, so once the migrations are applied to the shared database, every older API pointed at it fails on its next bill read or write ("column does not exist"). That covers an older server service, and any counter still on an old Desktop build that runs its own embedded API. For such a release: close Billing on every counter and stop the server service, take the dump below, apply the migrations by hand as the owner role (see above), update every machine, then start them. The release of 10 October 2026 is one (`DropUnusedColumnsAndIndexes`, `DropDraftEditLeases`). Migrations that only add things do not have this limit.

**Take a database dump before a release that carries migrations**, before the migrations are applied. With a PostgreSQL 18 client, and the connection values taken from OpenBao as described above:

```powershell
$env:PGPASSWORD = '<password from OpenBao>'
pg_dump --format=custom --no-owner --host <host> --port <port> --username <user> --dbname tally_wrapper_prod --file "tally_wrapper_prod_$(Get-Date -Format yyyyMMdd-HHmmss).dump"
Remove-Item Env:PGPASSWORD
pg_restore --list <the .dump file> | Select-Object -First 5   # proves the file is readable
```

A migration that removes data must also protect itself, because that step can be forgotten: it copies what it is about to drop into a side table in the same transaction, and its `Down` restores from that copy. `DropUnusedColumnsAndIndexes` (10 October 2026) is the model; see [10_database_schema.md §1.10](10_database_schema.md) for the tables it leaves and how to remove them.

### Desktop

- to upgrade a counter, replace its files with the new `publish\prod\` output (§2)
- a staged counter rollout is acceptable while contracts remain compatible and the release drops nothing from the schema
- nothing checks that a Desktop and the API it calls are the same build; when a contract changes, update the server and every counter together
- a counter coming from a build that ran its own embedded API opens on the limited-mode screen until a server address is saved in Settings → Server. An address that build had saved without using it is not taken up. Delete that older build's leftovers when the counter is upgraded: the folder `%LOCALAPPDATA%\ShowroomBilling\runtime\api` (about 130 MB) and the file `%APPDATA%\ShowroomBilling\database.*.local.json`, which holds that workstation's old database password and is no longer read by anything

---

## 12. Operational caveats

### Online-first consequence

This architecture is intentionally online-first. If the API is unavailable, the desktop cannot save bills. There is one API for the whole showroom: while the server PC is off or its service is stopped (the tray's **Exit Tray** stops it), every counter shows the limited-mode screen. Reasoning:

- no local durable business storage on the desktop
- invoice numbers are reserved at save time by the API's numbering service

This is not a bug — it's the direct consequence of the chosen architecture.

### Manual-only Tally consequence

If Tally is down, bills still save (they stay in `pending`). Nothing is auto-retried. The operator clicks Push again once Tally is back. This is intentional: every Tally interaction requires a human click, which keeps the causal chain simple for audit/support.

### Other caveats

- Tally XML remains a brittle integration boundary
- printer driver differences still exist even with better document generation
- a long push takes the full duration of Tally's response (5–30 seconds is normal); the operator's Push button stays disabled for that interval
- if the business later requires full offline billing continuity OR automatic retry, the architecture must be revisited explicitly
