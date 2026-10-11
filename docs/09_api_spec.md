# API Specification

This document defines the Tally Wrapper backend API surface.

Style:

- practical implementation contract
- not full OpenAPI
- concrete enough to build desktop and API in parallel

**Architectural note:** There is one API. It runs as a Windows Service on the Tally server, and every counter's Desktop is a client of it; a Desktop never starts an API. There is no separate Tally bridge. The API talks to Tally XML directly, synchronously, only when an operator clicks Push (or Refresh).

---

## 1. Conventions

- Base path: `/api`
- Payload format: JSON over HTTP unless stated otherwise. The server listens on port `5107`; its firewall rule admits the trusted LAN range only.
- IDs: UUIDs as strings
- Auth model: one workstation-write layer plus one admin layer.
  - **Device auth (`DeviceAuth:Mode`)** gates every normal **mutating** endpoint via `[Authorize(Policy = "Device")]`. In `TrustedLan` mode, which the server install sets, the API accepts mutating requests from loopback or configured `DeviceAuth:TrustedNetworks` CIDRs without a per-workstation token. Firewall scope must match the trusted CIDRs. In the default `LocalFile` mode, which a development run uses, the request must carry `X-Device-Token`, a 32-byte random secret in `%LOCALAPPDATA%\ShowroomBilling\device_token.txt` that the API and a Desktop on the same PC both read. The Desktop attaches the header only when its API address is loopback; a workstation on the LAN sends none. Reads remain unauthenticated.
  - **Admin token (`X-Admin-Token`)** — operator-entered passcode, 30 min TTL, lives in `AdminTokenStore`. Required for destructive/admin endpoints. Gated via `[Authorize(Policy = "Admin")]` (`AdminAuthenticationHandler`). Admin-gated routes do **not** additionally require the device token (the admin passcode is the stronger secret); if you're adding a new admin-only route, just the admin policy is fine.
  - **Maintenance token (`X-Maintenance-Token`)** — installer-generated local file token used only by the server tray against localhost maintenance DB endpoints. It is not a workstation credential.
- Timestamps: ISO 8601 UTC
- Error bodies: **RFC 7807 ProblemDetails** (`application/problem+json`) for all failure responses, emitted by a global `IExceptionHandler` (`DomainExceptionHandler`). There is no typed error body.

---

## 2. Desktop-facing endpoints

### 2.1 Runtime

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/runtime/health` | Desktop and server-tray health summary | API availability, database reachability and identity, pending migrations (see §4) |
| `POST` | `/api/runtime/database/maintenance/test` | Server tray: test a candidate connection string | `{ success, message }` |
| `PUT` | `/api/runtime/database/maintenance` | Server tray: verify and save the connection string | `{ provider, maskedConnectionString, configPath, isLocalOverridePresent, requiresApiRestart, environmentName, isEnvironmentOverridePresent, storageProtection }`; never the connection string itself |

The two database endpoints serve the tray only: the caller must be on the server PC (loopback) and send `X-Maintenance-Token`. No endpoint lets a workstation read or change the server's database connection. The saved connection is written, DPAPI-protected, to `database.<Environment>.local.json` in the server's data folder and takes effect when the service restarts.

Both accept only the Npgsql keyword form (`Host=...;Port=...;Database=...;Username=...;Password=...`). A `postgres://` or `postgresql://` URL is refused with `400` and a message that names the accepted form. Anything else Npgsql cannot parse, such as a pasted `psql '...'` command, is a `400` carrying the parser's message.

### 2.2 Bills

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `POST` | `/api/bills/drafts` | Create a saved bill (serial reserved here) | bill ID, current revision, state `pending`, assigned `InvoiceNumber` |
| `PUT` | `/api/bills/drafts/{billId}` | Update saved bill (appends new revision). A `posted` bill also needs `X-Admin-Token` | updated bill with new revision number |
| `GET` | `/api/bills/{billId}` | Load one bill with current revision and state | bill header, revision snapshot, state |
| `POST` | `/api/bills/batch` | Load several bills by id in one call (used to print a selection). Body: `{ billIds }`, at most 200; more is a `400`. | `{ bills[] }` with each bill's current revision |
| `GET` | `/api/bills` | Search bill history (paged). Query: `state`, `fromDate`, `toDate`, `skip`, `take`, `sort`, `includeTotal`, `search` (invoice number or party name). | `{ total, skip, take, items[] }` |
| `POST` | `/api/bills/{billId}/push` | **Synchronously** post one pending bill to Tally | bill with state `posted`, `failed`, or `reconciliation_required` |
| `POST` | `/api/bills/push-pending` | Loop over currently pending bills, pushing each synchronously | `{ matched, succeeded, failed, stoppedOnFailure, failedBillId, failureMessage, items[] }` |
| `POST` | `/api/bills/push-selected` | Sync-push a specific set of bills | same batch response shape |
| `POST` | `/api/bills/{billId}/void` | Void eligible bill | new state `voided` |
| `POST` | `/api/bills/{billId}/retry` | Re-post a failed bill (same sync path) | posting status: `{ billId, billState, lastErrorCode, lastErrorMessage, lastRemoteId }` |
| `POST` | `/api/bills/{billId}/repost` | Repost a posted or failed bill (same sync path) | posting status, same shape |
| `POST` | `/api/bills/{billId}/revise` | Create new pending bill superseding prior pending bill | the new bill |
| `GET` | `/api/bills/{billId}/audit` | Load audit/event trail | ordered events |
| `POST` | `/api/bills/{billId}/change-number` | **Admin** — rewrite invoice number | `ChangeBillNumberResponse` with warning flags |
| `POST` | `/api/bills/{billId}/mark-posted` | **Admin** — flip to posted without calling Tally | updated bill |
| `POST` | `/api/bills/{billId}/mark-pending` | **Admin** — flip posted/failed back to pending | updated bill |
| `DELETE` | `/api/bills/{billId}` | **Admin** — hard-delete bill + revisions, keep audit tombstone | delete summary |
| `POST` | `/api/bills/delete-selected` | **Admin** — batch hard-delete | per-item outcomes |
| `POST` | `/api/bills/synthetic-batch` | **Admin** — create a batch of backdated `pending` bills (see [`17_synthetic_batch.md`](17_synthetic_batch.md)) | `{ billCount, totalAmount }` |

#### Behavior

- Bill payload shape: `{ partyName, partyGstin, partyPhone, partyAddress, billDate, lines[], totals, notes }`. Each line also round-trips `grossWeight`, `lessWeight`, `wastagePercent`, `labourPerUnit`, `diamondRate`, `extra` for edit support. Totals: `{ subtotal, discountTotal, taxTotal, roundOff, grandTotal }`. Stored as `bill_revisions."SnapshotJson"` (jsonb) with projected helpers.
- `POST /api/bills/drafts` body `{ counterId?, payload, clientOperationId? }` returns 201 with a `Location` header. State starts at `pending`. A sales-invoice number is **taken from the sequence and written to the bill at this point**, in the same transaction. A request that repeats a `clientOperationId` returns the bill the first one created (`409` if the contents differ).
- `PUT /api/bills/drafts/{billId}` body `{ payload, expectedRevisionId? }` appends a new `bill_revisions` row and makes it the bill's current revision. Accepts any state except `posting`, `reconciliation_required`, `voided`, and `revised`. An edit to a `posted` bill also needs a valid `X-Admin-Token`: without one the answer is `403` with the title `Admin session required`; with one, the audit row names the admin. A `failed`, `pending` or `draft` bill needs only the device policy. If the prior state was `posted` or `failed`, the bill is reopened to `pending`; `EditedAfterPush` is set only when Tally holds the voucher (so a corrected first-push rejection is pushed again as a Create). When `expectedRevisionId` is supplied and another save has moved the bill on, the request returns `409`; a replay of a save that already landed (same payload, superseding the expected revision) returns the stored bill. A bill date in a different fiscal year from the bill's number is also `409`.
- `POST /api/bills/{billId}/push` body: `{ reason? }`. Synchronous: first runs a Tally company preflight. If Tally is unreachable or the configured active company is not open, returns `503 Tally unavailable` and leaves the bill unchanged. Otherwise it transitions the bill to `posting`, stamps the revision's `SubmittedAtUtc` (first push only) and `FinalizedAtUtc`, and makes one voucher HTTP attempt. A definite Tally response settles in `posted` or `failed`; a timeout, transport error, or unreadable response settles in `reconciliation_required` with `tally.outcome.unknown`. First pushes create Tally vouchers. Bills reopened after a prior successful push (`EditedAfterPush=true`) alter the original Tally voucher by `MASTER ID`; if that target cannot be resolved, the push fails with `TALLY_ALTER_TARGET_MISSING` without calling Tally. No queue or background posting.
- `POST /api/bills/push-pending` body: `{ reason?, maxBills? }`. Iterates oldest pending bills in created-at order, calls the sync push path for each, and returns aggregate counts. Only `posted` counts as success; the batch stops on the first failure, ambiguous outcome, or concurrent/ineligible state.
- `POST /api/bills/push-selected` body: `{ billIds, reason? }`. Same mechanics as push-pending but for an explicit set.
- `POST /api/bills/{billId}/retry` body: `{ reason? }`. Requires state `failed`. Runs the same sync push path. Retry and repost answer with the posting status `{ billId, billState, lastErrorCode, lastErrorMessage, lastRemoteId }`, not the bill; there is no separate endpoint that reads it.
- `POST /api/bills/{billId}/repost` body: `{ idempotencyKey, reason? }`. Requires state `posted` or `failed`. The key is required, capped at 128 characters, and is passed through as the create/import Tally `REMOTEID`; posted bills transition directly to `posting` without a persisted intermediate `pending` state. Plain unedited reposts keep create/import semantics; edited-after-push bills alter the old Tally voucher. 409 on any other state.
- `POST /api/bills/{billId}/revise` body `{ initialPayload? }`. Requires state `pending` or `draft`. Creates a new `pending` bill with its own invoice number and revision number 1, whose revision's `SupersedesRevisionId` points to the prior current revision; the prior bill moves to state `revised` and records `SupersededByBillId`.
- `POST /api/bills/{billId}/void` transitions to `voided` from `pending`, `draft`, or `failed`. It is `409` while Tally holds the bill's voucher.
- Admin-gated endpoints (`/change-number`, `/mark-posted`, `/mark-pending`, `DELETE`, `/delete-selected`, `/synthetic-batch`) require `X-Admin-Token` and are gated via `[Authorize(Policy = "Admin")]`. Behavior is detailed in §3 and in [`03_bill_state_machine.md`](03_bill_state_machine.md).
- All state transitions append `audit_events` rows (`bill.pending.created`, `bill.pending.updated`, `bill.edit.reopened`, `bill.push.requested`, `bill.push.cancelled`, `tally.posted`, `tally.failed`, `tally.outcome.unknown`, `bill.revised`, `bill.voided`, `bill.number.changed`, `bill.mark_posted`, `bill.mark_pending`, `bill.deleted`, `bill.posting.recovered`). Prior post history is never purged after an edit.
- 404 when the bill is unknown; 409 on state-gate violations; 503 when Tally preflight blocks a push/retry/repost; 400 on malformed request payloads **or payload-math validation failures** (see §2.2.1); 401 on missing/expired admin or device token; 403 when a `posted` bill is edited without an admin session.

#### 2.2.1 Payload validation on create / update / revise

`BillValidator` (Application layer) runs on every client-supplied `BillPayloadDto` before persistence. It cannot replay jewellery pricing (wastage %, labour per unit, gross/less weight) — the Desktop is the authority on that. It *does* enforce the summation-level invariants a client cannot forge:

- 1–500 lines; each line has non-empty `ItemName`, `Quantity > 0`, `Rate >= 0`, `LineTotal >= 0`
- bounded text fields and per-line `RawJson` (valid JSON, at most 64 KiB); optional jewellery numeric inputs and `Rate24Kt` must be non-negative
- `Σ LineTotal ≈ GrandTotal + Discount − RoundOff` (tolerance 0.05 × line count + ₹1). Line totals in this domain are **GST-inclusive**, so they sum to the billed amount net of discount/round-off — *not* to `Subtotal`.
- `GrandTotal ≈ Subtotal − Discount + Tax + RoundOff` (tolerance ₹1). Catches a client that sends tampered grand totals that don't match the tax math.
- `|RoundOff| <= ₹1`, `GrandTotal > 0`, non-negative discount/tax
- `BillDate` within `today - 1 year` to `today + 1 day`

Failure returns `400 Bill payload invalid` with the list of errors joined into `detail`. This runs on `POST /api/bills/drafts`, `PUT /api/bills/drafts/{billId}`, and `POST /api/bills/{billId}/revise` (only when the client supplies `InitialPayload` — a `null` `InitialPayload` replays the already-validated prior revision).

#### 2.2.2 Push concurrency guard

`POST /api/bills/{billId}/push` (and `/retry`, `/repost`, the batch push endpoints) first checks Tally company health. Tally must be reachable and the configured active company must be open. Batch push checks this once before iterating. If preflight fails, the API returns `503` and does not move any bill to `posting`.

After preflight, push atomically flips the bill to `posting` via a conditional update gated on `pending`, `draft`, or `failed` (plus `posted` only for explicit repost) **and on the revision the push loaded**. If another concurrent push won the flip, the request short-circuits and returns the current bill state without making a second Tally round-trip. If an edit landed during preflight, the push returns `409` and sends nothing, so Tally never receives an older revision than the one stored.

Every other bill transition saves through EF with `bills.xmin` as an optimistic concurrency token, so a request that read the bill before another transition committed fails with `409 Concurrent change` instead of overwriting it.

The flip is deliberately **not** wrapped in a transaction with the Tally call — `posting` must be visible in the DB before the HTTP round-trip so `StuckPostingRecoveryHostedService` can heal a row if the API crashes mid-call.

### 2.3 Numbering

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/numbering/preview` | Show next visible number without reservation | preview number, scope metadata |

`GET /api/numbering/preview` accepts optional `documentType` (default `sales_invoice`) and `fiscalYear` (default = current Indian fiscal year `YYYY-YY`). Returns `{ showroomId, fiscalYear, documentType, previewValue, formattedNumber, prefix, suffix }`.

There is no endpoint that reserves a number by itself. A number is taken by `CreateDraftAsync` (and by revise, for the new bill) when a bill is created, not by push, in the same transaction as the bill. Format: `{prefix}{fiscalYear}/{value}{suffix}`, the value zero-padded to the configured width (default 4). See [`04_numbering_and_idempotency.md`](04_numbering_and_idempotency.md).

### 2.4 Masters

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/masters/companies` | Load current company snapshot | companies, freshness metadata |
| `GET` | `/api/masters/ledgers` | Load ledger snapshot | ledgers, freshness metadata |
| `GET` | `/api/masters/stock-items` | Load stock item snapshot | items, freshness metadata |
| `GET` | `/api/masters/voucher-types` | Load voucher type snapshot | voucher types, freshness metadata |
| `POST` | `/api/masters/refresh` | **Synchronously** fetch fresh master data from Tally and write the snapshot | one or more `TallyMasterRefreshResult` rows |

The four reads take no query parameters. Each returns the whole current snapshot of that master type, in name order, with `{ masterType, batchId, fetchedAtUtc, itemCount, freshness }` as metadata.

A refresh stores the new snapshot and deletes the previous one of that master type in the same transaction, so a failed refresh leaves the old snapshot in place and no earlier snapshot is kept.

`POST /api/masters/refresh` body: `{ masterType?, requestedByActor? }`. `masterType` accepts `companies`, `ledgers`, `stock-items`, `voucher-types`, or null (all four). Any other value is rejected with `400`; the names are the constants in `Contracts.Masters.MasterTypes`. The API calls `ITallyMasterRefresher` synchronously — which uses `ITallyXmlClient` to query Tally, parses the response via `TallyXmlMasterSource`, and writes the snapshot via `IMasterSnapshotService`. The call blocks until Tally answers. There is no background polling, no timer, no scheduled refresh. **The only way master data updates is via this endpoint triggered by a UI click.**

Response per master type: `{ masterType, succeeded, itemCount, batchId?, errorMessage? }`.

### 2.5 Settings

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/settings` | Load effective settings | `{ settingsSource, summary, settings, cloudOwnedCategories, localOnlyCategories, updatedAtUtc, requiresInitialSetup }`; `settings` holds connection, numbering, print, ledgers and masters |
| `PUT` | `/api/settings` | Save settings payload | `{ settingsSource, summary, savedSections, updatedAtUtc }` |
| `POST` | `/api/settings/company/select` | Set active company. Body: `{ companyName }` | same shape as the save |
| `GET` | `/api/settings/print-layout` | Load margins, asset placements, watermark and structured page layout | `{ layout, updatedAtUtc }` |
| `PUT` | `/api/settings/print-layout` | Persist print layout | `{ layout, updatedAtUtc }` |

Settings writes validate required nested sections, host/company/ledger presence, port `1..65535`, timeout `5..300` seconds (`SettingLimits`), invoice padding `1..10`, print font size `8..18` and terms font size `7..16` (the range the renderer draws; `PrintLayoutDefaults` in Contracts), page margins `0..2.5 cm`, DB-aligned text lengths, and master JSON arrays (valid arrays, at most 2 MiB each). Print-layout values must be finite and remain inside the supported margin/placement ranges. Structured layouts require all known section keys exactly once, mandatory visibility, density in `compact|standard|comfortable`, spacing `0..20 mm`, border `0..4 pt`, and a null/known pin boundary. Watermark geometry must fit A4 and opacity is `0..100%`. Violations return `400 Bad request` before persistence.

### 2.6 Print assets

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/print-assets` | List uploaded assets (logo / signature / watermark) | `{ assets[] }` |
| `POST` | `/api/print-assets` | Upload asset (base64, ≤ 2 MB) | `{ id, assetKind, fileName, byteLength, ... }` |
| `GET` | `/api/print-assets/{id}` | Stream the raw image bytes | `File(bytes, contentType, fileName)` |
| `DELETE` | `/api/print-assets/{id}` | Remove asset | 204 / 404 if already gone |

Uploads require a plain file name (no path components), a decoded size of 1 byte–2 MiB, and PNG or JPEG signature bytes. The API detects and stores the actual MIME type instead of trusting caller-supplied metadata. Kestrel limits all request bodies to 5 MiB, preventing oversized base64 payloads from reaching model binding.

### 2.7 Client presence

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `POST` | `/api/clients/heartbeat` | Workstation heartbeat, sent every 30 seconds by Desktop | current registered client presence |
| `GET` | `/api/clients/presence` | Tray/status view of recently seen clients | localhost-only list with 2-minute TTL |

Heartbeat body: `{ deviceId, counterName, appVersion, machineName, userName }`. The desktop sends it non-blocking; failures never block billing. Presence is in-memory only, capped, and resets when the API restarts. The tray lists each client as counter, machine\user, address and last seen.

There are no edit locks: concurrent edits of one bill are caught by the bill's row version (409 "Concurrent change").

---

## 3. Admin and recovery endpoints

Admin-gated routes require a valid `X-Admin-Token` header (obtained via `POST /api/admin/unlock`). Tokens are SHA-256 hashed server-side, expire after 30 minutes, and can be revoked explicitly via logout. Each successful unlock also deletes the session rows that expired more than 30 days ago. Passcodes are PBKDF2 (SHA-256, 120 000 iterations).

Implementation: `AdminAuthenticationHandler` reads `X-Admin-Token`, validates via `IAdminAuthService.ValidateTokenAsync`, and builds a `ClaimsPrincipal` with an `admin_session_id` claim. Controllers opt in with `[Authorize(Policy = AdminPolicy.PolicyName)]`.

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/admin/passcode` | Report whether a passcode is configured | `{ isConfigured }` |
| `POST` | `/api/admin/passcode` | Create or rotate the admin passcode | 204; 403 if rotating without an `X-Admin-Token` session; 401 if current passcode wrong on rotation; 429 when throttled; 400 if new passcode shorter than 6 characters |
| `POST` | `/api/admin/unlock` | Exchange the passcode for a session token | `{ token, expiresAtUtc }`; 401 invalid, 409 not configured, **429 throttled** (with `Retry-After` header) |
| `POST` | `/api/admin/logout` | Revoke the current token | 204 |

After 4 consecutive failed unlock attempts, the API enters an exponential cooldown (2s, 4s, 8s, 16s, capped at 30s) per showroom. Subsequent attempts during the cooldown return `429 Too Many Requests` with a `Retry-After` header. A successful unlock resets the counter; the counter is in-memory only and resets on API restart. Failed attempts are recorded as `admin.unlock.failed` audit events.

When `DeviceAuth:Mode=TrustedLan` (the server install) and no admin passcode exists yet, the initial `POST /api/admin/passcode` setup is loopback-only: the first passcode has to be set on the server PC itself, for example from a Desktop running there. Later passcode rotations require both an unlocked admin session (`X-Admin-Token`) and the current passcode; wrong current passcodes share the unlock throttle and write `admin.passcode.change.failed` audit rows.

### 3.1 Bill admin actions

These are the admin-gated Bill endpoints listed in §2.2 (the synthetic batch is described in [`17_synthetic_batch.md`](17_synthetic_batch.md)):

- `POST /api/bills/{id}/change-number` — body `{ newInvoiceNumber, reason?, dryRun? }`. `newInvoiceNumber` is digits only. Returns `{ billId, oldInvoiceNumber, newInvoiceNumber, committed, leavesGap, tallyDiverges, sequenceNextValue, warningSummary }`. Blocked in `posting` and `reconciliation_required`. Desktop uses `dryRun=true` to surface warnings before the real commit.
- `POST /api/bills/{id}/mark-posted` — body `{ reason? }` (the API stores whatever reason it is given; the Desktop's prompt requires at least 4 characters). Allowed from `pending`, `draft`, `failed`, or `reconciliation_required`. Flips the bill to `posted` with a `bill.mark_posted` audit event. Does not call Tally.
- `POST /api/bills/{id}/mark-pending` — body `{ reason? }`, as above. Allowed from `posted`, `failed`, or `reconciliation_required`. Flips back to `pending`, clears `EditedAfterPush`, audit `bill.mark_pending`.
- `DELETE /api/bills/{id}` — body `{ reason?, dryRun? }`. Hard-deletes the bill + all revisions while preserving the append-only audit history and writing a final `bill.deleted` tombstone. Blocked in `posting` and `reconciliation_required`. `tallyDiverges` flags deletions where Tally may already have the voucher. A number freed at the end of the series is issued again unless Tally holds its voucher.
- `POST /api/bills/delete-selected` — body `{ billIds, reason? }`. Loops per bill and collects outcomes. Does NOT stop on first failure (unlike push-selected) — returns per-item `{ billId, deleted, tallyDiverges, reason }`.

---

## 4. Health and diagnostics endpoints

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/health/live` | Liveness probe | basic OK |
| `GET` | `/api/health/ready` | Readiness probe | whether PostgreSQL answers; `503` when it does not. Pending migrations are reported by `/api/runtime/health`, not here |
| `GET` | `/api/health/masters` | Master freshness summary | latest snapshot ages; `503` problem details when the database cannot be read |
| `GET` | `/api/health/tally-company` | Operator-triggered Tally reachability + active-company check | `{ status, tallyReachable, activeCompanyName, activeCompanyOpen, companyCount, checkedAtUtc, message }` |
| `GET` | `/api/runtime/health` | Cheap runtime health by default; full DB health only with `?forceDatabase=true` | `{ status, apiAvailable, databaseConfigured, databaseReachable, settingsLoadedFromApi, message, databaseIdentity?, expectedDatabaseIdentity?, databaseIdentityMatches?, databaseHealthSkipped, databaseHealthSkipReason?, activeClientCount, pendingMigrationCount? }` |

The DB-init and stuck-posting-recovery hosted services are bounded (DB init: 15s, recovery: 30s) and never throw, so a Postgres outage during startup leaves the API up. Their failures go to the API log; `/api/health/ready` and `/api/runtime/health?forceDatabase=true` report whether the database answers now.

`pendingMigrationCount` is how many of this build's schema migrations the database has not had: `null` until something has looked, `0` when up to date. Above zero the database is reachable but unusable, so a forced check then answers `status: "Unhealthy"` with a message that says so. The cheap probe carries the last count found (by the start-up migration or by a forced check) without asking the database, which is what lets the Desktop and the server tray keep showing it. Requests whose SQL PostgreSQL rejects for a missing column or table (SQLSTATE `42703` / `42P01`) are answered `503` with the title `Database upgrade needed` instead of a bare `500`.

`/api/runtime/health` intentionally avoids PostgreSQL unless `forceDatabase=true` is passed. This lets background desktop/server-tray health checks keep the API status current without a database round trip on every tick. Full DB truth is requested on startup, on explicit System Health refreshes, by the desktop's slower scheduled DB probe, and on every poll while the database is behind its migrations.

The API pushes nothing to the Desktop: there is no hub or event stream. The Desktop refreshes after every command it runs.

---

## 5. Ownership notes

| Concern | Primary owner |
|---|---|
| Pending-bill persistence | API |
| Number allocation | API (at save) |
| Voucher XML generation | API (`TallyXmlVoucherBuilder` in Infrastructure) |
| Tally posting transport | API (`TallyXmlClient` → Tally XML HTTP) |
| Final posting truth | API |
| Master snapshot truth | API (fetches on operator click) |
| Admin recovery | API |
| Stuck-post recovery on crash | API (`StuckPostingRecoveryHostedService`) |

---

## 6. Request / response expectations

### Push bill

Request body:

- optional `reason`

Response:

- `BillResponse` with `State = "posted"` (success), `State = "failed"` (definite rejection), or `State = "reconciliation_required"` (ambiguous write outcome)
- `InvoiceNumber` and `FiscalYear` (assigned at save)
- `CurrentRevision` with `submittedAtUtc` and `finalizedAtUtc` set

Typical duration: 1–10 seconds (however long Tally takes to answer). Not async.

Safe Tally XML reads sit behind a short Polly pipeline: 2 retry attempts with ~200 ms jittered exponential backoff for `HttpRequestException` and 5xx responses, sharing the configured timeout budget. Voucher writes are explicitly marked and excluded; one push/retry/repost operation makes at most one voucher HTTP attempt. Tally body `LINEERROR` / `EXCEPTIONS` responses are definite failures and are never retried automatically.

### Retry / Repost

Retry requires `failed` state; Repost requires `posted` or `failed` and a non-blank `idempotencyKey`. Both run the same synchronous push path as Push, but answer with the posting status `{ billId, billState, lastErrorCode, lastErrorMessage, lastRemoteId }` instead of the bill.

### Refresh masters

Request body: `{ masterType?, requestedByActor? }`.
Response: `TallyMasterRefreshResult[]` — one row per master type actually refreshed. The call blocks on the Tally XML queries; typical duration 2–30 seconds for all-four refresh depending on catalog size.
