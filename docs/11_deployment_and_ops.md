# Deployment and Operations

This document describes how Tally Wrapper is deployed and operated.

---

## 1. Deployment topology

### Components

- WPF desktop app on each counter workstation
- ASP.NET Core API process, **co-located on the same machine as TallyPrime** (reaches Tally's XML endpoint over localhost)
- optional `ShowroomBilling.ServerTray` app on the Tally server, running in the logged-in user's taskbar notification area
- PostgreSQL database (PostgreSQL 18 on an Oracle Cloud Infrastructure VPS in production; local Postgres remains available for development)
- TallyPrime running on the Tally host

No separate bridge process. No SignalR hub. No job queue.

### Communication paths

- Desktop -> API: HTTP(S) JSON
- Workstation Desktop -> server API: HTTP JSON to `http://<tally-server>:5107` when `DesktopBootstrap:ConnectionMode=Server`
- API -> PostgreSQL: direct DB connection (Npgsql)
- API -> TallyPrime: localhost HTTP/XML (synchronous, only on operator click)
- ServerTray -> API: localhost HTTP JSON for health and DB maintenance

Desktop never talks to Tally directly.

---

## 2. Desktop app deployment

### Target packaging

Preferred steady-state packaging:

- `MSIX` + `App Installer`

Acceptable pilot/early rollout option:

- `ClickOnce`, if it reduces initial deployment friction

### Desktop connection modes

`DesktopBootstrap:ConnectionMode` controls where the desktop sends API traffic:

- `LocalEmbedded` (default) — the desktop owns the API child process and uses `ApiBaseUrl`.
- `Server` — the desktop uses `ServerApiBaseUrl`, skips child-process startup, and does not create or send the local device token.

The typed local override file is `%APPDATA%\ShowroomBilling\desktop-bootstrap.local.json` and is intentionally limited to `connectionMode` and `serverApiBaseUrl`. It must not override child-process settings, database strings, Tally settings, or shared business behavior.

Operators normally change this from **Settings -> Database -> API Connection Mode** in the desktop. Choosing `Server` and saving writes the local override, then restarts Tally Wrapper so the next boot skips the embedded API. Choosing `LocalEmbedded` restores the old desktop-owned API path. The UI remembers the last non-localhost server URL, can test the server health endpoints, and can scan the local `/24` subnet for a Tally Wrapper API on port `5107`. Local embedded API and database details are hidden while the selected API location is `Server`; selecting `LocalEmbedded` reveals them. Editing remains disabled until the desktop is actually running in `LocalEmbedded` mode because server DB configuration is owned by the tray on the Tally server.

Fallback remains available per workstation: switching back to `LocalEmbedded` requires that workstation's own DB override and Tally host settings that can reach the Tally server by LAN name/IP.

### Child-process supervision

The Desktop spawns the API as a child under a Windows Job Object (`KillOnJobClose`). When the Desktop exits — gracefully or by crash — the API dies with it. The published desktop-owned API is launched with `ASPNETCORE_ENVIRONMENT=Production`, `DOTNET_ENVIRONMENT=Production`, and `--urls http://127.0.0.1:5107`, because the production desktop talks to the API over HTTP only. Development config overrides this to `Development` on `http://127.0.0.1:5108`, so VS Code/debug sessions do not attach to a production API already running on `5107`. If the API is already running (e.g. you're debugging it separately), the supervisor probes the configured API ports and skips spawning.

Config lives in `appsettings.json` under `ChildProcesses`. Only `ChildProcesses.Api` remains — the bridge entry is gone.

The database also carries its own identity marker in `public.database_identity` (`key = 'environment'`). Production databases must be marked `PROD`; development/test databases must be marked `DEV`. Runtime health compares this DB-owned value with the API environment and surfaces a `DB MISMATCH` warning on mismatch while still reporting PostgreSQL as reachable.

### Desktop update behavior

- updates should be centrally publishable
- operator update prompts should be predictable and low-friction
- client should reject incompatible backend schema/contracts with clear message rather than failing obscurely

### Local desktop persistence allowed

- app config needed to start
- cached auth/session tokens (admin unlock, short TTL)
- local UX preferences such as printer name and last PDF directory
- logs

No local durable business cache is allowed.

---

## 3. API deployment

### Recommended shape

- ASP.NET Core modular monolith
- **runs on the same machine as TallyPrime** (single-showroom deployment) — this is a hard topology constraint because the API dials Tally's localhost XML endpoint directly
- spawned as a child of the Desktop by default (see §2); for LAN deployments, deploy it as a Windows Service on the Tally server
- migration-safe startup: `DatabaseInitializationHostedService` applies EF migrations on boot

### Operational requirements

- structured logging to rolling log files (`%APPDATA%\ShowroomBilling\logs` for embedded mode; `C:\ProgramData\ShowroomBilling\logs` for server-service installs)
- `/api/health/live` and `/api/health/ready` endpoints
- `StuckPostingRecoveryHostedService` runs once on boot — any bill stranded in `posting` from a prior crash moves to `reconciliation_required` with a `bill.posting.recovered` audit event
- `OperationalDataRetentionHostedService` runs once after DB readiness and purges only admin sessions plus expired/released draft leases older than 30 days; accounting and audit records are excluded
- API errors are returned as RFC 7807 `application/problem+json` via a global `IExceptionHandler`. The only typed-body exception is `POST /api/draft-leases/acquire` (409 returns `DraftLeaseConflictResponse`). See [`09_api_spec.md`](./09_api_spec.md) §1.
- API admin endpoints are gated via ASP.NET Core authentication/authorization — scheme `AdminToken` + policy `Admin`. No framework-wide authentication is required for non-admin routes.
- Safe `ITallyXmlClient` reads have a Polly retry pipeline (2 attempts, ~200 ms jittered backoff, triggered on `HttpRequestException` + 5xx). Voucher writes are excluded to prevent duplicate Tally imports; their total timeout still comes from `Connection.TimeoutSeconds`.

### Windows Service server mode

Server mode is published with:

```powershell
.\tools\publish-server-tray.ps1
```

Copy `publish\server\TallyWrapper.Server.exe` to the Tally server and run it. The first run prompts for the trusted LAN CIDR, elevates via UAC, extracts the embedded API to `C:\ProgramData\ShowroomBilling\bin`, installs the Tally Wrapper API Windows Service, registers the tray EXE under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, starts the service, and leaves the tray running.

Running the same EXE again is idempotent. It checks the embedded API, service registration, service environment, firewall rule, maintenance token, tray startup registration, and service status, then repairs only missing or stale pieces.

The service install sets:

- `ASPNETCORE_URLS=http://0.0.0.0:5107`
- `SHOWROOM_BILLING_SERVICE_NAME=<ServiceName>`
- `SHOWROOM_BILLING_APPDATA=C:\ProgramData\ShowroomBilling`
- `Logging__File__Directory=C:\ProgramData\ShowroomBilling\logs`
- `Database__AutoMigrateOnStartup=true`
- `DeviceAuth__Mode=TrustedLan`
- `DeviceAuth__TrustedNetworks__0=<LanCidr>`

It also creates `C:\ProgramData\ShowroomBilling\maintenance_token.txt` and a Windows Firewall rule scoped to the configured LAN CIDR. The one-EXE installer uses the default LocalSystem service identity; DB configuration is written through the tray's localhost maintenance flow under `C:\ProgramData\ShowroomBilling`.

Server mode intentionally trusts clients inside the configured LAN CIDR for normal writes. Admin actions still require `X-Admin-Token`. DB override metadata, anonymous DB bootstrap, DB test, and initial admin-passcode setup are loopback-only in server mode.

### Server tray

`ShowroomBilling.ServerTray` is installed only on the Tally server and started at user login. It is a companion UI and bootstrapper, not the long-running API host. It uses Windows Service Control Manager for service state and control, so start/stop/restart still works when the API is stopped.

Tray features:

- show service state, API liveness, DB health, and recently seen workstation clients
- install/repair the server, start/stop/restart the API Windows Service, open the local health page, open logs/config/install-log folders, and copy the workstation server URL from either the tray menu or the main dashboard window
- configure/test/save DB settings through localhost-only maintenance endpoints using `maintenance_token.txt`; DB maintenance result dialogs include a **Copy** action for supportable error messages
- exit the tray by stopping the API Windows Service first, then closing the companion UI
- no Tally polling; Tally is checked only by operator-triggered push/refresh/System Health flows

The tray's normal **Exit Tray** action stops `ShowroomBilling.Api` through Windows Service Control Manager before closing the companion tray UI. This is the server shutdown path for the Tally host.

### Startup hosted services

- `DatabaseInitializationHostedService` — applies EF migrations.
- `StuckPostingRecoveryHostedService` — one-shot classification of stuck `posting` bills as reconciliation-required.
- `OperationalDataRetentionHostedService` — bounded purge of stale auth-session and draft-lease rows.
- `SequenceSelfHealHostedService` and `DatabaseWarmupHostedService` — bounded sequence repair and read-path warmup after database readiness.

These are bounded, single-pass startup tasks. There is no recurring posting, Tally polling, or retention worker after startup completes.

---

## 4. PostgreSQL deployment

### Production host

Production uses PostgreSQL 18 self-hosted on an Oracle Cloud Infrastructure VPS. It is not a managed database service: the team owns the operating system, PostgreSQL installation and upgrades, backups, restore testing, certificates, monitoring, and storage capacity.

Configure the API or server tray with an Npgsql connection string shaped like:

```text
Host=<oracle-vps-host>;Port=5432;Database=tally_wrapper_prod;Username=<app-role>;Password=<secret>;SSL Mode=<vps-required-mode>;Minimum Pool Size=<min>;Maximum Pool Size=<max>
```

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
- numbering sequences and reservations
- audit events
- master snapshots (companies, ledgers, stock items, voucher types)
- leases/locks (draft edit leases)
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

## 5. ~~SignalR~~ (removed)

Tally Wrapper no longer hosts a SignalR hub. The desktop refreshes explicitly after every command it runs; there's no background push channel to maintain.

---

## 6. ~~Local Tally bridge~~ (removed)

There is no separate bridge process. `ITallyPoster` and `ITallyMasterRefresher` live inside the API's Infrastructure layer. The API process is the only component that speaks Tally XML.

If the API is deployed on a different machine than Tally, bridging is the operator's problem — e.g. an SSH tunnel or VPN. The default / supported topology is API on the Tally host.

---

## 7. Tally host assumptions

- TallyPrime is reachable on `http://{host}:{port}` from the API process (typically `127.0.0.1:9000`)
- host and port are configured via cloud settings `Connection.Host` / `Connection.Port`
- selected company exists in Tally and is open/available
- firewall rules permit API -> TallyPrime traffic

---

## 8. Logs and diagnostics

### Desktop logs

Should capture:

- startup/bootstrap status
- API connectivity failures
- print failures
- admin/recovery actions where appropriate

Location: `%APPDATA%\ShowroomBilling\logs` (rolling files).

### API logs

Should capture:

- request traces with correlation IDs
- bill finalization failures
- numbering allocation failures
- state transitions (bill.* audit events are also on disk via DB)
- Tally call outcomes (`tally.posted`, `tally.failed`, `tally.outcome.unknown`, `TALLY_TRANSPORT_UNKNOWN`, `TALLY_TIMEOUT`, etc.) and safe-read retry attempts emitted by `Microsoft.Extensions.Http.Resilience`
- admin and recovery actions
- `StuckPostingRecoveryHostedService` startup report

Location: `%APPDATA%\ShowroomBilling\logs` when spawned by the Desktop, or `C:\ProgramData\ShowroomBilling\logs` when installed as the server service.

---

## 9. Health checks

### API endpoints

- `/api/health/live` — liveness
- `/api/health/ready` — DB connectivity + migration readiness
- `/api/health/masters` — freshness of companies/ledgers/stock-items/voucher-types snapshots
- `/api/health/tally-company` — operator-triggered Tally reachability + active-company check
- `/api/runtime/health` — cheap runtime status by default; pass `?forceDatabase=true` for a PostgreSQL-backed DB/identity probe
- `/api/clients/presence` — localhost-only in-memory list of workstations seen in the last 2 minutes

Desktop background health polling uses cheap runtime probes so it does not add a database round trip on every tick. Full DB health, Tally-company health, and master freshness are requested on startup, explicit System Health refreshes, database setup waits, and a slower scheduled probe. The Oracle VPS PostgreSQL service remains an always-on infrastructure dependency, so availability, resource use, and storage pressure must be monitored at the VPS/database layer.

### What's NOT a health check anymore

- bridge heartbeat / last-seen (no bridge exists)
- Tally-job queue depth (no queue exists)
- SignalR hub health (no hub exists)

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
- `DatabaseInitializationHostedService` applies migrations on startup
- preserve backward-compatibility window if desktop rollout is staged

### Desktop

- staged counter rollout acceptable if contracts remain compatible
- if incompatible, block with clear required-update message

---

## 12. Operational caveats

### Online-first consequence

This architecture is intentionally online-first. If the API is unavailable, the desktop cannot save bills. Reasoning:

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
