# Documentation Index

Tally Wrapper is two processes on one Windows box: Desktop (WPF) + API (ASP.NET Core). The API calls Tally XML directly, synchronously, only when an operator clicks Push or Refresh. There is no bridge process, no job queue, no polling loop, no automatic retry.

## Current architecture

- [03_bill_state_machine.md](./03_bill_state_machine.md) — bill states and transitions (current Tally Wrapper).
- [04_numbering_and_idempotency.md](./04_numbering_and_idempotency.md) — invoice numbering, serial-at-save, renumbering, `REMOTEID`.
- [05_tally_integration_contract.md](./05_tally_integration_contract.md) — Desktop ↔ API ↔ Tally responsibility split. Single-process API; no bridge.
- [06_tally_xml_golden_path.md](./06_tally_xml_golden_path.md) — canonical Tally XML write + read paths (in-process).
- [07_printing_spec.md](./07_printing_spec.md) — operator-visible printing behavior.
- [08_settings_catalog.md](./08_settings_catalog.md) — per-field settings catalog.
- [09_api_spec.md](./09_api_spec.md) — backend API surface (desktop-facing, admin, health).
- [10_database_schema.md](./10_database_schema.md) — PostgreSQL 18 schema (bills, revisions, numbering, audit, masters, leases).
- [11_deployment_and_ops.md](./11_deployment_and_ops.md) — deployment topology, OpenBao database-secret workflow, shared PostgreSQL/PgBouncer test environment, Oracle VPS operations, health checks, startup recovery, upgrade flow.
- [14_settings_storage_contract.md](./14_settings_storage_contract.md) — what lives locally vs in the cloud DB.
- [15_ui_design_reference.md](./15_ui_design_reference.md) — design tokens, component inventory, keyboard rules.
- [17_synthetic_batch.md](./17_synthetic_batch.md) — synthetic Batch Data Scheduler (V1-ported, admin-gated).

> Gaps in the numbering (01, 02, 12, 13, 16) are intentional: those slots were V1-era drafts that didn't survive into Tally Wrapper. The surviving docs kept their original numbers so cross-links and PR archaeology don't rot. New docs should claim the next free slot rather than backfill.
