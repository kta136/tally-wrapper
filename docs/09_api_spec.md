# API Specification

This document defines the Tally Wrapper backend API surface.

Style:

- practical implementation contract
- not full OpenAPI
- concrete enough to build desktop and API in parallel

**Architectural note:** Tally Wrapper is a two-process deployment — Desktop + API. There is no separate Tally bridge. The API talks to Tally XML directly, synchronously, only when an operator clicks Push (or Refresh). All bridge-facing routes from the previous version (`/api/bridge/session/*`, `/api/bridge/jobs/*`, `/api/bridge/masters/*`) have been removed.

---

## 1. Conventions

- Base path: `/api`
- Payload format: JSON over HTTP(S) unless stated otherwise. The server-service LAN install binds HTTP on the trusted LAN only.
- IDs: UUIDs as strings
- Auth model: one workstation-write layer plus one admin layer.
  - **Device auth (`DeviceAuth:Mode`)** gates every normal **mutating** endpoint via `[Authorize(Policy = "Device")]`. In default `LocalFile` mode, the desktop/API share `X-Device-Token`, a 32-byte random secret stored at `%LOCALAPPDATA%\ShowroomBilling\device_token.txt`. In server-service `TrustedLan` mode, the API accepts mutating requests from loopback or configured `DeviceAuth:TrustedNetworks` CIDRs without a per-workstation token. Firewall scope must match the trusted CIDRs. Reads remain unauthenticated.
  - **Admin token (`X-Admin-Token`)** — operator-entered passcode, 30 min TTL, lives in `AdminTokenStore`. Required for destructive/admin endpoints. Gated via `[Authorize(Policy = "Admin")]` (`AdminAuthenticationHandler`). Admin-gated routes do **not** additionally require the device token (the admin passcode is the stronger secret); if you're adding a new admin-only route, just the admin policy is fine.
  - **Maintenance token (`X-Maintenance-Token`)** — installer-generated local file token used only by the server tray against localhost maintenance DB endpoints. It is not a workstation credential.
- Timestamps: ISO 8601 UTC
- Error bodies: **RFC 7807 ProblemDetails** (`application/problem+json`) for all failure responses, emitted by a global `IExceptionHandler` (`DomainExceptionHandler`). The only deliberate exception is `POST /api/draft-leases/acquire`, which still returns the typed `DraftLeaseConflictResponse { Error, ExistingLease }` on 409 because the Desktop parses it as a record.

---

## 2. Desktop-facing endpoints

### 2.1 Runtime / bootstrap

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/runtime/bootstrap` | Load startup/runtime payload for desktop | active showroom, counter settings, connection status summary, feature flags |
| `GET` | `/api/runtime/health` | Desktop health summary | API availability, master freshness, limited-mode reasons |
| `GET` | `/api/runtime/database` | Current DB override metadata | loopback-only in server mode; override source, masked connection string, file paths |
| `PUT` | `/api/runtime/database` | **Admin** — save DB override | updated DB metadata |
| `PUT` | `/api/runtime/database/bootstrap` | First anonymous DB override setup | loopback-only in server mode; rejects when an override already exists |
| `POST` | `/api/runtime/database/test` | Test a candidate DB string | loopback-only in server mode |
| `POST` | `/api/runtime/database/maintenance/test` | Server tray DB test | localhost-only; requires `X-Maintenance-Token` |
| `PUT` | `/api/runtime/database/maintenance` | Server tray DB save/recovery | localhost-only; requires `X-Maintenance-Token` |

In `TrustedLan` server mode, DB override metadata, anonymous DB bootstrap, DB test, and first admin-passcode setup are restricted to loopback. Workstations cannot use the LAN trust boundary to inspect or reconfigure the server database.

### 2.2 Bills

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `POST` | `/api/bills/drafts` | Create a saved bill (serial reserved here) | bill ID, current revision, state `pending`, assigned `InvoiceNumber` |
| `PUT` | `/api/bills/drafts/{billId}` | Update saved bill (appends new revision) | updated bill with new revision number |
| `GET` | `/api/bills/{billId}` | Load one bill with current revision and state | bill header, revision snapshot, state |
| `GET` | `/api/bills` | Search bill history (paged). Query: `state`, `fromDate`, `toDate`, `skip`, `take`, `sort`, `includeTotal`, `search` (invoice number or party name). | `{ total, skip, take, items[] }` |
| `POST` | `/api/bills/{billId}/push` | **Synchronously** post one pending bill to Tally | bill with state `posted`, `failed`, or `reconciliation_required` |
| `POST` | `/api/bills/push-pending` | Loop over currently pending bills, pushing each synchronously | `{ matched, succeeded, failed, stoppedOnFailure, failedBillId, failureMessage, items[] }` |
| `POST` | `/api/bills/push-selected` | Sync-push a specific set of bills | same batch response shape |
| `POST` | `/api/bills/{billId}/void` | Void eligible bill | new state `voided` |
| `POST` | `/api/bills/{billId}/retry` | Re-post a failed bill (same sync path) | posting status snapshot |
| `POST` | `/api/bills/{billId}/repost` | Repost a posted or failed bill (same sync path) | posting status snapshot |
| `GET` | `/api/bills/{billId}/posting-status` | Current state + last-post metadata | `{ billId, billState, lastErrorCode, lastErrorMessage, lastRemoteId }` |
| `POST` | `/api/bills/{billId}/revise` | Create new pending bill superseding prior pending bill | new bill/revision identifiers |
| `GET` | `/api/bills/{billId}/audit` | Load audit/event trail | ordered events |
| `POST` | `/api/bills/{billId}/change-number` | **Admin** — rewrite invoice number | `ChangeBillNumberResponse` with warning flags |
| `POST` | `/api/bills/{billId}/mark-posted` | **Admin** — flip to posted without calling Tally | updated bill |
| `POST` | `/api/bills/{billId}/mark-pending` | **Admin** — flip posted/failed back to pending | updated bill |
| `DELETE` | `/api/bills/{billId}` | **Admin** — hard-delete bill + revisions, keep audit tombstone | delete summary |
| `POST` | `/api/bills/delete-selected` | **Admin** — batch hard-delete | per-item outcomes |

#### Behavior

- Bill payload shape: `{ partyName, partyGstin, partyPhone, partyAddress, billDate, lines[], totals, notes }`. Each line also round-trips `grossWeight`, `lessWeight`, `wastagePercent`, `labourPerUnit`, `diamondRate`, `extra` for edit support. Totals: `{ subtotal, discountTotal, taxTotal, roundOff, grandTotal }`. Stored as `bill_revisions.snapshot_json` (jsonb) with projected helpers.
- `POST /api/bills/drafts` returns 201 with a `Location` header. State starts at `pending`. A sales-invoice number is **reserved and written to `bills.invoice_number` at this point** (idempotency key `draft:{billId}`).
- `PUT /api/bills/drafts/{billId}` appends a new `bill_revisions` row and repoints `bills.current_revision_id`. Accepts any state except `posting`, `reconciliation_required`, `voided`, and `revised`. If the prior state was `posted` or `failed`, the bill is reopened to `pending` with `EditedAfterPush = true`.
- `POST /api/bills/{billId}/push` body: `{ reason? }`. Synchronous: first runs a Tally company preflight. If Tally is unreachable or the configured active company is not open, returns `503 Tally unavailable` and leaves the bill unchanged. Otherwise marks the revision `submitted_at = finalized_at = now`, transitions the bill to `posting`, and makes one voucher HTTP attempt. A definite Tally response settles in `posted` or `failed`; a timeout, transport error, or unreadable response settles in `reconciliation_required` with `tally.outcome.unknown`. First pushes create Tally vouchers. Bills reopened after a prior successful push (`EditedAfterPush=true`) alter the original Tally voucher by `MASTER ID`; if that target cannot be resolved, the push fails with `TALLY_ALTER_TARGET_MISSING` without calling Tally. No queue or background posting.
- `POST /api/bills/push-pending` body: `{ reason?, maxBills? }`. Iterates oldest pending bills in created-at order, calls the sync push path for each, and returns aggregate counts. Only `posted` counts as success; the batch stops on the first failure, ambiguous outcome, or concurrent/ineligible state.
- `POST /api/bills/push-selected` body: `{ billIds, reason? }`. Same mechanics as push-pending but for an explicit set.
- `POST /api/bills/{billId}/retry` body: `{ reason? }`. Requires state `failed`. Runs the same sync push path.
- `POST /api/bills/{billId}/repost` body: `{ idempotencyKey, reason? }`. Requires state `posted` or `failed`. The key is required, capped at 128 characters, and is passed through as the create/import Tally `REMOTEID`; posted bills transition directly to `posting` without a persisted intermediate `pending` state. Plain unedited reposts keep create/import semantics; edited-after-push bills alter the old Tally voucher. 409 on any other state.
- `GET /api/bills/{billId}/posting-status` returns only `{ billId, billState, lastErrorCode, lastErrorMessage, lastRemoteId }`; obsolete queue/job compatibility fields were removed.
- `POST /api/bills/{billId}/revise` creates a new `pending` bill with `revision_no = 1` whose revision's `supersedes_revision_id` points to the prior current revision; the prior bill moves to state `revised` and records `superseded_by_bill_id`.
- `POST /api/bills/{billId}/void` transitions to `voided` from `pending`, `draft`, or `failed`.
- Admin-gated endpoints (`/change-number`, `/mark-posted`, `/mark-pending`, `DELETE`, `/delete-selected`) require `X-Admin-Token` and are gated via `[Authorize(Policy = "Admin")]`. Behavior is detailed in §4 and in [`03_bill_state_machine.md`](03_bill_state_machine.md).
- All state transitions append `audit_events` rows (`bill.pending.created`, `bill.pending.updated`, `bill.edit.reopened`, `bill.push.requested`, `tally.posted`, `tally.failed`, `tally.outcome.unknown`, `bill.revised`, `bill.voided`, `bill.number.changed`, `bill.mark_posted`, `bill.mark_pending`, `bill.deleted`, `bill.posting.recovered`). Prior post history is never purged after an edit.
- 404 when the bill is unknown; 409 on state-gate violations; 503 when Tally preflight blocks a push/retry/repost; 400 on malformed request payloads **or payload-math validation failures** (see §2.2.1); 401 on missing/expired admin or device token.

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

After preflight, push atomically flips the bill to `posting` via a conditional update gated on `pending`, `draft`, or `failed` (plus `posted` only for explicit repost). If another concurrent push won the flip, the request short-circuits and returns the current bill state without making a second Tally round-trip.

The flip is deliberately **not** wrapped in a transaction with the Tally call — `posting` must be visible in the DB before the HTTP round-trip so `StuckPostingRecoveryHostedService` can heal a row if the API crashes mid-call.

### 2.3 Numbering

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/numbering/preview` | Show next visible number without reservation | preview number, scope metadata |
| `GET` | `/api/numbering/scopes` | Load configured numbering scopes | scope list and prefixes/suffixes |
| `POST` | `/api/numbering/reserve` | Atomically reserve next number (idempotent) | reservation id + formatted number |

`GET /api/numbering/preview` accepts optional `documentType` (default `sales_invoice`) and `fiscalYear` (default = current Indian fiscal year `YYYY-YY`). Returns `{ showroomId, fiscalYear, documentType, previewValue, formattedNumber, prefix, suffix }`.

`POST /api/numbering/reserve` requires `{ idempotencyKey, documentType, fiscalYear?, reservedForReference? }` and responds with the reservation. Invoice numbers are reserved by `CreateDraftAsync` on every new bill, not by push. Format: `{prefix}{fiscalYear}/{value:D4}{suffix}`.

### 2.4 Masters

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/masters/companies` | Load current company snapshot | companies, freshness metadata |
| `GET` | `/api/masters/ledgers` | Load ledger snapshot | ledgers, freshness metadata |
| `GET` | `/api/masters/stock-items` | Load stock item snapshot | items, freshness metadata |
| `GET` | `/api/masters/voucher-types` | Load voucher type snapshot | voucher types, freshness metadata |
| `POST` | `/api/masters/refresh` | **Synchronously** fetch fresh master data from Tally and write the snapshot | one or more `TallyMasterRefreshResult` rows |

`POST /api/masters/refresh` body: `{ masterType?, requestedByActor? }`. `masterType` accepts `companies`, `ledgers`, `stock-items`, `voucher-types`, or null (all four). The API calls `ITallyMasterRefresher` synchronously — which uses `ITallyXmlClient` to query Tally, parses the response via `TallyXmlMasterSource`, and writes the snapshot via `IMasterSnapshotService`. The call blocks until Tally answers. There is no background polling, no timer, no scheduled refresh. **The only way master data updates is via this endpoint triggered by a UI click.**

Response per master type: `{ masterType, succeeded, itemCount, batchId?, errorMessage? }`.

### 2.5 Settings

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/settings` | Load effective settings catalog values | connection, print, numbering, mappings, admin flags |
| `PUT` | `/api/settings` | Save settings payload | saved sections, validation messages, runtime impact summary |
| `POST` | `/api/settings/company/select` | Set active company | updated runtime/settings summary |
| `GET` | `/api/settings/print-layout` | Load margins, asset placements, watermark and structured page layout | `{ layout, updatedAtUtc }` |
| `PUT` | `/api/settings/print-layout` | Persist print layout | `{ layout, updatedAtUtc }` |

Settings writes validate required nested sections, host/company/ledger presence, port `1..65535`, timeout `1..300`, invoice padding `1..10`, print font sizes `6..24`, DB-aligned text lengths, and master JSON arrays (valid arrays, at most 2 MiB each). Print-layout values must be finite and remain inside the supported margin/placement ranges. Structured layouts require all known section keys exactly once, mandatory visibility, density in `compact|standard|comfortable`, spacing `0..20 mm`, border `0..4 pt`, and a null/known pin boundary. Watermark geometry must fit A4 and opacity is `0..100%`. Violations return `400 Bad request` before persistence.

### 2.6 Print assets

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/print-assets` | List uploaded assets (logo / signature / watermark) | `{ assets[] }` |
| `POST` | `/api/print-assets` | Upload asset (base64, ≤ 2 MB) | `{ id, assetKind, fileName, byteLength, ... }` |
| `GET` | `/api/print-assets/{id}` | Stream the raw image bytes | `File(bytes, contentType, fileName)` |
| `GET` | `/api/print-assets/{id}/metadata` | Metadata only | asset record |
| `DELETE` | `/api/print-assets/{id}` | Remove asset | 204 / 404 if already gone |

Uploads require a plain file name (no path components), a decoded size of 1 byte–2 MiB, and PNG or JPEG signature bytes. The API detects and stores the actual MIME type instead of trusting caller-supplied metadata. Kestrel limits all request bodies to 5 MiB, preventing oversized base64 payloads from reaching model binding.

### 2.7 Draft edit leases

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `POST` | `/api/draft-leases/acquire` | Acquire (or renew) a bill edit lock | `DraftLeaseResponse` (or 409 `DraftLeaseConflictResponse` with current owner) |
| `POST` | `/api/draft-leases/{leaseId}/renew` | Extend the lease TTL | `DraftLeaseResponse` |
| `POST` | `/api/draft-leases/{leaseId}/release` | Release the lease | 204 |
| `GET` | `/api/draft-leases/bill/{billId}` | Get the active lease for a bill, if any | `DraftLeaseResponse?` |

Leases use a 2-minute TTL. The database enforces exactly one live lease per bill via a unique partial index on `(BillId) WHERE ReleasedAtUtc IS NULL`.

### 2.8 Client presence

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `POST` | `/api/clients/heartbeat` | Workstation heartbeat, sent every 30 seconds by Desktop | current registered client presence |
| `GET` | `/api/clients/presence` | Tray/status view of recently seen clients | localhost-only list with 2-minute TTL |

Heartbeat body: `{ deviceId, counterName, appVersion, connectionMode, machineName, userDisplayName }`. The desktop sends it non-blocking; failures never block billing. Presence is in-memory only, capped, and resets when the API restarts.

---

## 3. ~~Tally bridge-facing endpoints~~ (removed)

Tally Wrapper no longer has a bridge process. All bridge-facing endpoints (`/api/bridge/session/*`, `/api/bridge/jobs/*`, `/api/bridge/masters/*`, `/api/bridge/heartbeat`, `/api/bridge/config`) have been deleted. The API talks to Tally directly in-process.

---

## 4. Admin and recovery endpoints

Admin-gated routes require a valid `X-Admin-Token` header (obtained via `POST /api/admin/unlock`). Tokens are SHA-256 hashed server-side, expire after 30 minutes, and can be revoked explicitly via logout. Passcodes are PBKDF2 (SHA-256, 120 000 iterations).

Implementation: `AdminAuthenticationHandler` reads `X-Admin-Token`, validates via `IAdminAuthService.ValidateTokenAsync`, and builds a `ClaimsPrincipal` with an `admin_session_id` claim. Controllers opt in with `[Authorize(Policy = AdminPolicy.PolicyName)]`. The session is also published on `HttpContext.Items[AdminTokenConstants.HttpContextItemKey]` so `GET /api/admin/session` can return it without re-reading the claims.

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/admin/passcode` | Report whether a passcode is configured | `{ isConfigured }` |
| `POST` | `/api/admin/passcode` | Create or rotate the admin passcode | 204; 401 if current passcode wrong on rotation; 400 if new passcode shorter than 6 characters |
| `POST` | `/api/admin/unlock` | Exchange the passcode for a session token | `{ token, expiresAtUtc }`; 401 invalid, 409 not configured, **429 throttled** (with `Retry-After` header) |
| `POST` | `/api/admin/logout` | Revoke the current token | 204 |

After 4 consecutive failed unlock attempts, the API enters an exponential cooldown (2s, 4s, 8s, 16s, capped at 30s) per showroom. Subsequent attempts during the cooldown return `429 Too Many Requests` with a `Retry-After` header. A successful unlock resets the counter; the counter is in-memory only and resets on API restart. Failed attempts are recorded as `admin.unlock.failed` audit events.

When `DeviceAuth:Mode=TrustedLan` and no admin passcode exists yet, the initial `POST /api/admin/passcode` setup is loopback-only. Later passcode rotations still require the existing admin credential.
| `GET` | `/api/admin/session` (admin) | Inspect the session behind the current token | `AdminSessionInfoResponse` |
| `GET` | `/api/draft-leases/active` (admin) | List every live draft lease across the showroom | `DraftLeaseListResponse` |
| `POST` | `/api/draft-leases/{leaseId}/force-release` (admin) | Force-release a stale lease with a reason | 204; audits `lease.force_released` |

### 4.1 Bill admin actions

These are the five admin-gated Bill endpoints listed in §2.2:

- `POST /api/bills/{id}/change-number` — body `{ newInvoiceNumber, reason?, dryRun? }`. Returns `{ billId, oldInvoiceNumber, newInvoiceNumber, committed, leavesGap, tallyDiverges, reservationOrphaned, sequenceNextValue, warningSummary }`. Blocked in `posting` and `reconciliation_required`. Desktop uses `dryRun=true` to surface warnings before the real commit.
- `POST /api/bills/{id}/mark-posted` — body `{ reason }` (reason ≥ 4 chars). Allowed from `pending`, `draft`, `failed`, or `reconciliation_required`. Flips the bill to `posted` with a `bill.mark_posted` audit event. Does not call Tally.
- `POST /api/bills/{id}/mark-pending` — body `{ reason }` (reason ≥ 4 chars). Allowed from `posted`, `failed`, or `reconciliation_required`. Flips back to `pending`, clears `EditedAfterPush`, audit `bill.mark_pending`.
- `DELETE /api/bills/{id}` — body `{ reason?, dryRun? }`. Hard-deletes the bill + all revisions while preserving the append-only audit history and writing a final `bill.deleted` tombstone. Blocked in `posting` and `reconciliation_required`. `tallyDiverges` flags deletions where Tally may already have the voucher. Keeps the invoice-number reservation row so retried drafts cannot collide.
- `POST /api/bills/delete-selected` — body `{ billIds, reason? }`. Loops per bill and collects outcomes. Does NOT stop on first failure (unlike push-selected) — returns per-item `{ billId, deleted, tallyDiverges, reason }`.

---

## 5. Health and diagnostics endpoints

| Method | Path | Purpose | Summary response |
|---|---|---|---|
| `GET` | `/api/health/live` | Liveness probe | basic OK |
| `GET` | `/api/health/ready` | Readiness probe | DB, migrations |
| `GET` | `/api/health/masters` | Master freshness summary | latest snapshot ages |
| `GET` | `/api/health/tally-company` | Operator-triggered Tally reachability + active-company check | `{ status, tallyReachable, activeCompanyName, activeCompanyOpen, companyCount, checkedAtUtc, message }` |
| `GET` | `/api/health/startup` | Startup hosted-service status (DB migration + stuck-posting recovery) | `{ startedAtUtc, databaseReady, databaseError?, recoveryRan, recoveryHealedCount, recoveryError? }` |
| `GET` | `/api/runtime/health` | Cheap runtime health by default; full DB health only with `?forceDatabase=true` | `{ status, apiAvailable, databaseConfigured, databaseReachable, settingsLoadedFromApi, message, databaseIdentity?, expectedDatabaseIdentity?, databaseIdentityMatches?, databaseHealthSkipped, databaseHealthSkipReason?, activeClientCount }` |

`/api/health/startup` exposes whether the DB-init and stuck-posting-recovery hosted services completed cleanly on this API boot. Both services are bounded (DB init: 60s timeout, recovery: 30s) and never throw — a Postgres outage during startup leaves the API up but with `databaseReady = false` so the Desktop can show a degraded-mode banner. State is in-memory and resets on each API restart.

`/api/runtime/health` intentionally avoids PostgreSQL unless `forceDatabase=true` is passed. This lets background desktop/server-tray health checks keep the API status current without unnecessary managed-Postgres wakeups. Full DB truth is requested on startup, explicit System Health refreshes, database setup waits, and the desktop's slower scheduled DB probe.

`/api/health/bridge` and `/api/health/tally-jobs` have been removed along with the bridge and the job queue.

---

## 6. ~~SignalR events~~ (removed)

Tally Wrapper no longer hosts a SignalR hub. The previous `/hubs/system` endpoint and all event broadcasting (`JobHint`, `tally.posted`, etc.) are gone. The desktop refreshes explicitly after every command it runs (which is the only case it cares about), so real-time push is not needed.

---

## 7. Ownership notes

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

## 8. Request / response expectations

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

Same shape as Push. Retry requires `failed` state; Repost requires `posted` or `failed`. Both run the same synchronous push path.

### Refresh masters

Request body: `{ masterType?, requestedByActor? }`.
Response: `TallyMasterRefreshResult[]` — one row per master type actually refreshed. The call blocks on the Tally XML queries; typical duration 2–30 seconds for all-four refresh depending on catalog size.
