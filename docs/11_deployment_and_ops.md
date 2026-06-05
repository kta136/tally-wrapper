# Deployment and Operations

This document describes how V2 is deployed and operated.

---

## 1. Deployment topology

### Components

- WPF desktop app on each counter workstation
- ASP.NET Core API process, **co-located on the same machine as TallyPrime** (reaches Tally's XML endpoint over localhost)
- optional `ShowroomBilling.ServerTray` app on the Tally server, running in the logged-in user's taskbar notification area
- PostgreSQL database (can be local on the same machine for single-showroom deployment, or remote for multi-showroom)
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

Operators normally change this from **Settings -> Database -> API Connection Mode** in the desktop. Choosing `Server` and saving writes the local override, then restarts Billing so the next boot skips the embedded API. Choosing `LocalEmbedded` restores the old desktop-owned API path. The UI remembers the last non-localhost server URL, can test the server health endpoints, and can scan the local `/24` subnet for a Showroom Billing API on port `5107`. The same Database section shows the local embedded DB override; editing is disabled while the desktop is currently running in `Server` mode because server DB configuration is owned by the tray on the Tally server.

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
- `StuckPostingRecoveryHostedService` runs once on boot — any bill stranded in `posting` from a prior crash is flipped back to `pending` with a `bill.posting.recovered` audit event
- API errors are returned as RFC 7807 `application/problem+json` via a global `IExceptionHandler`. The only typed-body exception is `POST /api/draft-leases/acquire` (409 returns `DraftLeaseConflictResponse`). See [`09_api_spec.md`](./09_api_spec.md) §1.
- API admin endpoints are gated via ASP.NET Core authentication/authorization — scheme `AdminToken` + policy `Admin`. No framework-wide authentication is required for non-admin routes.
- `ITallyXmlClient` has a Polly retry pipeline (2 attempts, ~200 ms jittered backoff, triggered on `HttpRequestException` + 5xx). Total timeout still comes from `Connection.TimeoutSeconds`.

### Windows Service server mode

Server mode is published with:

```powershell
.\tools\publish-server-tray.ps1
```

Copy `publish\server\ShowroomBilling.Server.exe` to the Tally server and run it. The first run prompts for the trusted LAN CIDR, elevates via UAC, extracts the embedded API to `C:\ProgramData\ShowroomBilling\bin\ShowroomBilling.Api.exe`, installs the `ShowroomBilling.Api` Windows Service, registers the tray EXE under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, starts the service, and leaves the tray running.

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
- configure/test/save DB settings through localhost-only maintenance endpoints using `maintenance_token.txt`
- exit the tray by stopping the API Windows Service first, then closing the companion UI
- no Tally polling; Tally is checked only by operator-triggered push/refresh flows

The tray's normal **Exit Tray** action stops `ShowroomBilling.Api` through Windows Service Control Manager before closing the companion tray UI. This is the server shutdown path for the Tally host.

### Startup hosted services

- `DatabaseInitializationHostedService` — applies EF migrations.
- `StuckPostingRecoveryHostedService` — one-shot recovery of stuck `posting` bills.

Both are single-pass on boot. There is no background worker after startup completes.

---

## 4. PostgreSQL deployment

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

- automated backups
- point-in-time recovery if possible
- alerting on connection failures and storage pressure
- clear migration strategy for schema changes

---

## 5. ~~SignalR~~ (removed)

V2 no longer hosts a SignalR hub. The desktop refreshes explicitly after every command it runs; there's no background push channel to maintain.

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
- Tally call outcomes (`tally.posted`, `tally.failed`, `TALLY_HTTP`, `TALLY_TIMEOUT`, etc.) and any Polly retry attempts emitted by `Microsoft.Extensions.Http.Resilience`
- admin and recovery actions
- `StuckPostingRecoveryHostedService` startup report

Location: `%APPDATA%\ShowroomBilling\logs` when spawned by the Desktop, or `C:\ProgramData\ShowroomBilling\logs` when installed as the server service.

---

## 9. Health checks

### API endpoints

- `/api/health/live` — liveness
- `/api/health/ready` — DB connectivity + migration readiness
- `/api/health/masters` — freshness of companies/ledgers/stock-items/voucher-types snapshots
- `/api/clients/presence` — localhost-only in-memory list of workstations seen in the last 2 minutes

### What's NOT a health check anymore

- bridge heartbeat / last-seen (no bridge exists)
- Tally-job queue depth (no queue exists)
- SignalR hub health (no hub exists)

### Tally reachability

Tally reachability is only verified when the operator clicks Push or Refresh. A failed Push surfaces `TALLY_HTTP`, `TALLY_TIMEOUT`, or `TALLY_NOT_CONFIGURED` in the bill's `LastErrorCode`. The System Health dialog's Tally card shows a neutral "manual" status — it cannot pre-probe without making unsolicited Tally calls, which the architecture forbids.

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
- `StuckPostingRecoveryHostedService` reconciles any bill stranded in `posting` back to `pending` for operator retry
- data is in PostgreSQL; nothing lost

### Tally outage recovery

- bills continue to be saved (numbering, saving, audit all live in the API + DB)
- clicking Push surfaces `TALLY_HTTP` until Tally is back
- when Tally returns, operator clicks Retry on failed bills

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
