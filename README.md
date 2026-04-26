# Showroom Billing V2

This repository is the V2 rewrite of the showroom billing system documented in [`docs/`](docs/README.md).

## Architecture

Two processes:

- **`ShowroomBilling.Desktop`** — WPF counter app. Edits bills, prints invoices, drives admin workflows. No business state stored locally.
- **`ShowroomBilling.Api`** — ASP.NET Core modular monolith. Owns bills, numbering, audit trail, and the Tally integration. Runs on the same Windows machine as TallyPrime and dials its localhost XML endpoint directly.

All Tally interaction is **synchronous and operator-initiated**:

- Clicking **Push** on a bill → API builds voucher XML, POSTs it to Tally, and returns the posted-or-failed result in one HTTP round-trip. No queue, no background worker. The HTTP client has a short retry pipeline (2 attempts, ~200 ms jittered backoff) for transient network blips; the total timeout stays bounded by the cloud-settings `Connection.TimeoutSeconds`.
- Clicking **Refresh from Tally** in Settings → API fetches the master snapshot from Tally and writes it inline. No timer.

PostgreSQL is the system of record for bills, revisions, numbering, audit events, master snapshots, leases, print assets, and cloud settings. Bills round-trip through EF Core migrations; `StuckPostingRecoveryHostedService` reconciles any bill stranded in `posting` after a crash on the next API boot.

## Database Configuration

The API reads `ConnectionStrings:Postgres` from configuration at startup. Operators can update the local machine override from **Desktop → Settings → Database**. The override is stored at `%APPDATA%\ShowroomBilling\database.{Environment}.local.json`; after saving, restart the API or Desktop app so the EF Core connection pool uses the new connection string.

VS Code/debug launches use the `Development` API environment on `http://localhost:5108`. Published EXE launches use the `Production` API environment on `http://localhost:5107`. The database itself owns the final marker in `public.database_identity`; set it to `DEV` on the dev DB and `PROD` on the prod DB. The desktop status bar shows that DB-owned value (`DB DEV`, `DB PROD`, or `DB UNSET`).

## Building

```
dotnet build
dotnet test
```

Both the Desktop exe and the API exe emit under `src/*/bin/Debug/net10.0{,-windows}/`. VS Code launch profiles are in `.vscode/launch.json` — the "Desktop + API" compound is the usual one.

## Deployment

Single Windows machine per showroom, running TallyPrime + API + Desktop. Spawning is handled by `ChildProcessSupervisor` on Desktop startup under a Windows Job Object with `KillOnJobClose`.

## Docs

- [`docs/03_bill_state_machine.md`](docs/03_bill_state_machine.md) — bill states and transitions
- [`docs/05_tally_integration_contract.md`](docs/05_tally_integration_contract.md) — desktop/API/Tally responsibility split
- [`docs/09_api_spec.md`](docs/09_api_spec.md) — API surface contract
- [`docs/10_database_schema.md`](docs/10_database_schema.md) — PostgreSQL schema
- [`docs/11_deployment_and_ops.md`](docs/11_deployment_and_ops.md) — deployment topology + health + recovery
- [`docs/17_synthetic_batch.md`](docs/17_synthetic_batch.md) — synthetic Batch Data Scheduler (V1-ported, admin-gated)
- [`DEV_SETUP.md`](DEV_SETUP.md) — local build/run commands
- [`CLAUDE.md`](CLAUDE.md) — conventions, design-system rules, and gotchas for AI coding agents working in this repo
