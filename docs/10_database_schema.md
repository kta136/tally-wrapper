# Database Schema

This document defines the target V2 PostgreSQL schema at the level needed to implement the first production version.

Design principles:

- PostgreSQL is the system of record
- immutable finalized bill revisions
- cloud-owned numbering and retry state
- no local durable business storage

---

## 1. Major tables

### 1.1 `bills`

Business document lineage. Present as of the `BillsAndRevisions` migration; `tenant_id` is deferred (single-tenant foundation).

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | Bill lineage ID |
| `showroom_id` | UUID | Showroom scope |
| `counter_id` | UUID nullable | Originating counter/device |
| `bill_type` | text | Initially `sales` |
| `current_revision_id` | UUID nullable | Points to latest active revision |
| `state` | text | Current bill state (see `03_bill_state_machine.md`) |
| `invoice_number` | text nullable | Reserved at save (`CreateDraftAsync`), not at push |
| `fiscal_year` | text nullable | Indian fiscal year of the reserved number (`YYYY-YY`) |
| `superseded_by_bill_id` | UUID nullable | Set when a `revise` produces a new draft lineage |
| `edited_after_push` | boolean NOT NULL DEFAULT false | Set when an admin reopens a posted/failed bill for edit; cleared by mark-pending |
| `created_by` | UUID nullable | Operator/user |
| `created_at_utc` | timestamptz | |
| `updated_at_utc` | timestamptz | |
| `voided_at_utc` | timestamptz nullable | |

Constraints / indexes:

- PK on `id`
- index `(showroom_id, state, created_at_utc desc)` for history/list queries
- unique partial index `(showroom_id, fiscal_year, invoice_number) WHERE invoice_number IS NOT NULL` — hard duplicate-number guard on finalized bills

### 1.2 `bill_revisions`

Immutable snapshots of bill content. Present as of the `BillsAndRevisions` migration.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | Revision ID |
| `bill_id` | UUID FK -> `bills.id` ON DELETE CASCADE | |
| `revision_no` | integer | Monotonic within bill |
| `snapshot_json` | jsonb | Full bill payload |
| `totals_json` | jsonb | Derived totals snapshot |
| `party_name` | text nullable | Search/helper projection |
| `bill_date` | date nullable | Search/helper projection |
| `grand_total` | numeric(18,3) | Search/helper projection |
| `created_at_utc` | timestamptz | |
| `supersedes_revision_id` | UUID nullable | Prior draft lineage link |
| `submitted_at_utc` | timestamptz nullable | Set during push |
| `finalized_at_utc` | timestamptz nullable | Set during push (same instant as submitted in this phase) |

Constraints / indexes:

- unique `(bill_id, revision_no)`
- index `(bill_id, created_at_utc)`
- FK `bill_id -> bills.id` with `ON DELETE CASCADE`
- GIN index on `snapshot_json` only if needed later

### 1.3 `invoice_sequences`

Cloud-owned numbering state. Present as of the `InvoiceNumbering` migration; `tenant_id` is deferred (current schema is single-tenant).

| Column | Type | Notes |
|---|---|---|
| `showroom_id` | UUID | |
| `fiscal_year` | text | Indian fiscal year format `YYYY-YY` (April–March) |
| `document_type` | text | Currently `sales_invoice` |
| `next_value` | bigint | Next reservable integer |
| `updated_at_utc` | timestamptz | |

Constraints / indexes:

- PK `(showroom_id, fiscal_year, document_type)`
- row-lock via `SELECT ... FOR UPDATE` during allocation (Npgsql path); InMemory test path uses a plain fetch

### 1.3a `invoice_number_reservations`

Idempotent record of issued numbers. Each successful `POST /api/numbering/reserve` writes exactly one row.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | Reservation ID |
| `idempotency_key` | text | Unique per logical reservation intent |
| `showroom_id` | UUID | |
| `fiscal_year` | text | |
| `document_type` | text | |
| `reserved_value` | bigint | Integer allocated from `invoice_sequences` |
| `formatted_number` | text | `{prefix}{fiscalYear}/{value:D4}{suffix}` |
| `reserved_for_reference` | text nullable | Caller hint (e.g. draft bill ID) |
| `reserved_at_utc` | timestamptz | |

Constraints / indexes:

- unique index on `idempotency_key` — retries with the same key return the original row, never double-allocate
- index `(showroom_id, fiscal_year, document_type)` for scope-level lookups

### 1.4 ~~`tally_posting_jobs`~~ (removed — dropped by the `DropPostingJobsAndBridgeSession` migration)

V2 posting is synchronous and inline inside `BillService.PushAsync`. There is no queue, no outbox, no lease, no claim loop. Post outcomes are recorded as `tally.posted` / `tally.failed` audit events on the bill itself. Retry and repost re-run the same synchronous push path; edited-after-push bills alter the prior Tally voucher using the pre-edit `tally.posted.details.tallyMasterId` (or numeric legacy `remoteId`) instead of creating a new voucher.

### 1.5 ~~`tally_posting_attempts`~~ (removed — dropped alongside `tally_posting_jobs`)

Per-attempt forensics now live in the audit trail. `BillPostingStatusResponse` reconstructs the fields the desktop needs (`LastErrorCode`, `LastErrorMessage`, `LastRemoteId`) by reading the most recent `tally.failed` / `tally.posted` audit events for the bill. New successful Tally posts include `details.tallyAction` (`Create` or `Alter`) and `details.tallyMasterId` when available so future edit pushes can target the old voucher safely.

### 1.6 `audit_events`

General audit/event trail.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | |
| `tenant_id` | UUID | |
| `entity_type` | text | `bill`, `job`, `settings`, `admin_unlock`, etc. |
| `entity_id` | UUID or text | |
| `event_type` | text | |
| `actor_type` | text | `desktop_user`, `api`, `system`, `admin` |
| `actor_id` | UUID or text nullable | |
| `payload_json` | jsonb | |
| `created_at` | timestamptz | |

Indexes:

- index `(entity_type, entity_id, created_at desc)`
- index `(event_type, created_at desc)`

### 1.7 `tally_master_snapshot_batches`

Snapshot version header for master-data pulls.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | Snapshot batch ID |
| `tenant_id` | UUID | Deferred; current schema is single-tenant. |
| `showroom_id` | UUID | |
| `master_type` | text | `companies`, `ledgers`, `stock_items`, `voucher_types` |
| `fetched_at` | timestamptz | |
| `status` | text | `active`, `superseded`, `failed` |
| `item_count` | integer | Row count at ingestion time for quick freshness summaries |

Indexes:

- index `(showroom_id, master_type, fetched_at)`
- index `(showroom_id, master_type, status)` — used when selecting the single active batch

Supersession: on ingestion, any prior row with the same `(showroom_id, master_type)` and `status = active` is flipped to `superseded` in the same transaction before the new batch is inserted.

### 1.8 Master snapshot rows

Separate tables for query simplicity (all present as of the `MasterSnapshotsIngestion` migration):

- `tally_company_snapshots` — `name`, `is_active`, `raw_json`
- `tally_ledger_snapshots` — `name`, `parent`, `primary_group`, `is_revenue`, `gstin`, `raw_json`
- `tally_stock_item_snapshots` — `name`, `alias`, `base_unit`, `hsn_code`, `karat`, `raw_json`
- `tally_voucher_type_snapshots` — `name`, `parent_type`, `is_deemed_positive`, `raw_json`

Common columns:

- `id` UUID PK
- `batch_id` FK -> `tally_master_snapshot_batches.id` with `ON DELETE CASCADE`
- `tenant_id` (deferred as above)
- `showroom_id`
- normalized value columns per type
- `raw_json` jsonb for source metadata

Indexes per type:

- index `(showroom_id, name)`
- index `(batch_id)`

### 1.9 `draft_edit_leases`

Server-owned replacement for current local edit locks.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | Lease ID |
| `bill_id` | UUID FK | Draft being edited |
| `revision_id` | UUID FK nullable | Optional current draft revision |
| `owner_device_id` | UUID | Desktop/counter claiming edit |
| `owner_user_id` | UUID nullable | |
| `lease_expires_at` | timestamptz | |
| `created_at` | timestamptz | |
| `updated_at` | timestamptz | |

Indexes:

- unique partial index on active lease per `bill_id`
- index `(lease_expires_at)`

### 1.10 `admin_passcodes`

One row per showroom. Configured via `POST /api/admin/passcode`; verified via `POST /api/admin/unlock`.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | |
| `showroom_id` | UUID | Unique index |
| `salt` | text | base64 16-byte salt |
| `hash` | text | base64 32-byte PBKDF2-SHA256 output (120_000 iterations) |
| `iterations` | int | PBKDF2 iterations (for future rotations) |
| `updated_at_utc` | timestamptz | |

### 1.11 `admin_sessions`

One row per admin unlock. Token is returned once at unlock time; the DB only stores its SHA-256 hash.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | |
| `showroom_id` | UUID | |
| `token_hash` | text | Unique index; SHA-256 of the raw token |
| `issued_at_utc` | timestamptz | |
| `expires_at_utc` | timestamptz | 30 minutes after issue |
| `revoked_at_utc` | timestamptz nullable | Set by `/api/admin/logout` |

### 1.12 `print_assets`

Logo / signature images uploaded by the operator. Content stored inline (bytea) with a 2 MB cap.

| Column | Type | Notes |
|---|---|---|
| `id` | UUID PK | |
| `showroom_id` | UUID | |
| `asset_kind` | text | `logo` or `signature` |
| `file_name` | text | |
| `content_type` | text | e.g. `image/png` |
| `content` | bytea | raw bytes |
| `byte_length` | bigint | |
| `created_at_utc` | timestamptz | |

Also: `cloud_settings.print_layout_json` (jsonb, default `{}`) holds margins + logo/signature `PrintLayoutAssetPlacement` records referring to `print_assets.id`.

### 1.13 ~~`bridge_registrations`~~ (removed — dropped by the `DropBridgeRegistrationsAndSourceBridgeId` migration)

The table was part of the original bridge design. It was dropped once the API absorbed the bridge; the API talks to Tally directly in-process. `tally_master_snapshot_batches.source_bridge_id` was dropped in the same migration.

---

## 2. Relations and uniqueness rules

### 2.1 Key relationships

- `bills.current_revision_id` -> `bill_revisions.id`
- `bill_revisions.bill_id` -> `bills.id`
- `draft_edit_leases.bill_id` -> `bills.id`
- `tally_*_snapshots.batch_id` -> `tally_master_snapshot_batches.id`
- `print_assets` referenced by `cloud_settings.print_layout_json`

### 2.2 Uniqueness rules

- one revision number per bill
- one issued invoice number per `(showroom_id, fiscal_year)` scope (partial unique index on `bills`)
- one logical numbering row per scope
- active edit lease uniqueness per draft bill
- one idempotency key per numbering reservation

---

## 3. Lock / lease semantics

### 3.1 Draft edit leases

- desktop requests lease when opening editable draft
- lease expires automatically if client disappears
- admin recovery can release stale lease
- this replaces the current local-only lock recovery pattern with a server-owned model

### 3.2 ~~Posting job leases~~ (removed)

V2 has no posting jobs and no leases on the Tally side. Push is synchronous — the API holds the Tally connection for the duration of one HTTP request. If the API crashes mid-call, `StuckPostingRecoveryHostedService` flips any bill stranded in `posting` back to `pending` on the next API boot with a `bill.posting.recovered` audit event.

---

## 4. Current-system carryovers vs redesign

### Carry over from current system

- explicit bill history and status tracking
- numbering preview vs reservation distinction
- operational recovery needs for locks and failed posting jobs
- master-data snapshot concept, though current app handled it locally

### Redesign in V2

- local repository tables become cloud PostgreSQL tables
- same-bill posted mutation model becomes immutable revision model
- **Tally posting is synchronous in-process** — no job queue, no outbox, no retry schedule. Posting outcomes are recorded as audit events.
- local master caches become API-owned server snapshots, refreshed synchronously on operator click (no bridge, no timer)
- local edit locks become server leases
- posted bills can be reopened for edit by admins (sets `bills.edited_after_push = true`)
