# Development Setup

## Prerequisites

- Windows with the .NET 10 SDK selected by [global.json](global.json).
- PowerShell 7 and Docker for the scratch app. The [database tests](#database-tests) can also use a dedicated PostgreSQL 18 server.
- EF Core 10 CLI for migrations. Install it with `dotnet tool install --global dotnet-ef --version "10.*"`.

## Database

The scratch script creates, migrates and marks its own PostgreSQL 18 database. The API owns the database connection. On an installed server, test and save the runtime connection in the tray, then restart the API service. For site provisioning and backups, see [deployment and operations](docs/11_deployment_and_ops.md#4-postgresql-deployment).

For a manual migration, supply the target database explicitly using a role that owns the schema.

```powershell
dotnet ef database update --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --connection "<connection-string>"
```

`tools\run-scratch.ps1` does this for the scratch database. For production, migrations are applied by hand before a new build is started; the procedure is in [docs/11 §11](docs/11_deployment_and_ops.md#11-upgrade-flow).

The API's committed `appsettings.json` holds only a placeholder connection string (`Host=localhost;Port=5432;Database=tally_wrapper;Username=postgres;Password=postgres`).

If the database is unreachable, the API and the Desktop still start, but DB-backed settings and migrations remain unavailable until the database is reachable. The Desktop then shows the limited-mode screen and tells the operator to check the database connection in the tray on the server PC.

Each database carries its own environment marker. The scratch script sets it automatically. For a manually provisioned database, run this after migration, using `DEV` for development or `PROD` for production.

```sql
insert into public.database_identity (key, value, updated_at_utc)
values ('environment', 'DEV', current_timestamp)
on conflict (key) do update
set value = excluded.value, updated_at_utc = excluded.updated_at_utc;
```

The Database indicator shows the marker. Health reports a mismatch if the API environment expects a different value.

## Build

```powershell
dotnet restore ShowroomBilling.sln
dotnet build ShowroomBilling.sln
dotnet test --solution ShowroomBilling.sln
```

`global.json` selects Microsoft Testing Platform for the xUnit test projects. Use the explicit
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

Every test that touches the database runs on PostgreSQL 18, the production version; there is no in-memory stand-in. A plain `dotnet test` runs them with everything else. `PostgresFixture` starts one server for the run the first time a test asks for a database, migrates a template database once, and gives each test its own copy of it, dropped afterwards. The fixture selects its own test connection independently of the API's saved settings.

The server is the first of these that applies:

1. `SHOWROOM_BILLING_POSTGRES_TEST_CONNECTION`, when set: an already-running PostgreSQL 18 whose role may create databases, for example `Host=<host>;Port=<port>;Database=postgres;Username=postgres;Password=<password>`.
2. A throwaway `postgres:18` container started with the `docker` command, when the current Docker context is a remote engine (`ssh://` or `tcp://`). Testcontainers can only drive a local engine. The container is stopped when the run ends, and stops itself after two hours if the run was killed.
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

The development API serves its OpenAPI document at `http://localhost:5108/openapi/v1.json`. Use `GET /api/health/live` for process liveness, `GET /api/health` for database and runtime status, and `GET /api/health/tally-company` to check Tally. The [API specification](docs/09_api_spec.md) covers the business routes and their authorization requirements.

## The server (API + tray)

The server is a single EXE that carries the API, installs or repairs its Windows Service and stays as the tray:

```powershell
.\tools\publish-server-tray.ps1
```

It produces `publish\server\TallyWrapper.Server.exe`. Installing it, what the install sets, upgrading and removing it are in [docs/11 §3](docs/11_deployment_and_ops.md#server-install); the tray in [§5](docs/11_deployment_and_ops.md#5-server-tray); pointing a workstation at the server in [§2](docs/11_deployment_and_ops.md#server-address). Do not run the tray exe on a development PC: starting it installs the service.

A Desktop with no saved server address uses `DesktopBootstrap:ApiBaseUrl` from its `appsettings.json` (`http://localhost:5107`, or `http://localhost:5108` in Development). The environment variable `SHOWROOM_DESKTOP_DesktopBootstrap__ApiBaseUrl` overrides both that and a saved address.

## Run the desktop shell

The Desktop is a client: it needs an API that is already running and never starts one. When billing is paused, the banner gives the cause. **System health** opens the latest check results, **Check again** retries, and **Server settings** opens the workstation’s server address.

- `.\tools\run-scratch.ps1` starts the API on `127.0.0.1:5108` and then the Desktop, both on a throwaway database, and stops the API when the app closes. See [Run against a scratch database](#run-against-a-scratch-database).
- In VS Code, the `Desktop + API` compound starts both. The `Desktop` configuration alone needs an API you started yourself.
- By hand, start the API as above and then:

```powershell
$env:DOTNET_ENVIRONMENT='Development'
dotnet run --project src/ShowroomBilling.Desktop
```

A Desktop started by `dotnet run` or F5 reads this PC's real settings folder. If the PC has a server address saved in Settings → Server, a Desktop started by `dotnet run` uses that address in place of `http://localhost:5108`, so on a PC that also runs the real app it talks to the real server. `run-scratch.ps1` avoids this by giving the Desktop an empty settings folder, and the VS Code `Desktop` launch configuration by setting `SHOWROOM_DESKTOP_DesktopBootstrap__ApiBaseUrl` to the development API; for `dotnet run`, set that variable for the session.

For the operator screens and shortcuts, see the [Desktop UI reference](docs/15_ui_design_reference.md). Printing and workstation preferences are covered in the [printing specification](docs/07_printing_spec.md). The [settings catalog](docs/08_settings_catalog.md) lists each setting and where to edit it.

## Run against a scratch database

On a PC that also runs the real app, the dev build must not find the saved server address or database connection, and test bills must not reach TallyPrime. This starts it so that none of that can happen:

```powershell
.\tools\run-scratch.ps1
```

The script uses the active Docker context for a new scratch database. Pass `-DockerContext <name>` to select another engine. Later runs reuse the saved context, including for reset and removal. If a scratch database predates this setting, select its original context on the first run. Local settings and connection information are stored under the ignored `artifacts\scratch` folder. It sets up the following components.

- a `postgres:18` container named `tw-scratch-postgres` with a random password, migrated to the current schema and marked `DEV`;
- empty settings folders, handed to the app through `SHOWROOM_BILLING_APPDATA` and `SHOWROOM_BILLING_LOCALAPPDATA`, with `SHOWROOM_BILLING_POSTGRES` pointing at the container. The API gives that variable precedence over every saved connection string;
- a stand-in Tally (`tools\scratch-tally.ps1`) on `127.0.0.1:9000`, which is where a new database expects Tally. It serves three companies (the first is the one a new database names as active, so Push works at once), nine ledgers, five stock items and three voucher types, and accepts vouchers. A bill whose party name or note contains `FAIL` is refused with a line error (the bill becomes `failed`); one containing `UNKNOWN` is kept but answered with a server error (the bill needs reconciliation). It also answers "which voucher has this id" and "which vouchers carry this number" from the vouchers of the current run, so Mark as Pushed after `UNKNOWN`, then an edit and a push, alters that voucher. It matches an alter the way TallyPrime does, by id and the voucher's current date, and like TallyPrime it creates a new voucher when they do not match. `artifacts\scratch\tally.log` lists what the app sent;
- the dev build's API on `127.0.0.1:5108`, started by the script and stopped when the app closes. The Desktop it then starts finds that API through its Development address.

The script refuses to start the app when something already listens on port 9000 (probably TallyPrime; pass `-AllowRealTally` if that is intended) or on the dev API port 5108 (the Desktop would connect to that API, whatever database it uses, in place of the one the script starts).

The app starts like a fresh install: no admin passcode, placeholder company. The database keeps its data between runs until the container stops. `-NoLaunch` prepares without starting the app, `-Reset` starts over with an empty database, `-Remove` deletes the container and the folder. For the debugger’s settings and address overrides, see [VS Code F5](#vs-code-f5).

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
