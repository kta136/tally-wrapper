# CLAUDE.md

Notes for Claude (or any AI coding agent) working in this repo. Read [README.md](README.md) and [DEV_SETUP.md](DEV_SETUP.md) first — those describe the system; this file describes how to *work* on it.

## What this repo is

Windows desktop billing app for a jewellery showroom. Each counter runs the Desktop (WPF, `net10.0-windows`), a client of one ASP.NET Core API (`net10.0`) that runs as a Windows service on the Tally server. That API owns PostgreSQL 18 hosted on an Oracle Cloud Infrastructure VPS and dials TallyPrime's local XML endpoint. The Desktop never starts or contains an API.

- **Solution:** `ShowroomBilling.sln` · target framework is **`.NET 10`** across every project (Desktop is `net10.0-windows`).
- **Hosts:** `src/ShowroomBilling.Api`, `src/ShowroomBilling.Desktop`, and `src/ShowroomBilling.ServerTray` (WinForms installer and tray that installs the API as a Windows service on the Tally server and holds its database connection). A Desktop's only connection setting is the server address (`DesktopBootstrap:ApiBaseUrl`, edited in Settings → Server).
- **Layers:** `Contracts` (DTOs, shared between hosts), `Application`, `Infrastructure` (EF Core + Tally XML), `Printing` (QuestPDF templates). (There is no `Domain` project — business rules live in `Application` / `Infrastructure`.)
- **Tests:** `tests/ShowroomBilling.Tests` (API + infra) and `tests/ShowroomBilling.Desktop.Tests` (ViewModels).

## Authoritative docs — pointers, not restatements

| Topic | File |
|---|---|
| Dev prerequisites, build, run, OpenBao production secret, Oracle VPS/PostgreSQL 18 | [DEV_SETUP.md](DEV_SETUP.md) |
| Bill state machine (draft → pending → posting → posted/failed/voided) | [docs/03_bill_state_machine.md](docs/03_bill_state_machine.md) |
| Numbering & idempotency (reservation, `idempotency_key`, `draft:{billId}`) | [docs/04_numbering_and_idempotency.md](docs/04_numbering_and_idempotency.md) |
| Tally integration responsibility split (sync, operator-initiated) | [docs/05_tally_integration_contract.md](docs/05_tally_integration_contract.md) |
| API surface | [docs/09_api_spec.md](docs/09_api_spec.md) |
| DB schema | [docs/10_database_schema.md](docs/10_database_schema.md) |
| Deployment topology, server install, recovery | [docs/11_deployment_and_ops.md](docs/11_deployment_and_ops.md) |
| Settings storage contract (cloud-owned vs local-only) | [docs/14_settings_storage_contract.md](docs/14_settings_storage_contract.md) |
| **UI design reference + token → WPF mapping** | [docs/15_ui_design_reference.md](docs/15_ui_design_reference.md) |
| Design bundle (HTML/CSS/React mockup) | [docs/design/](docs/design/) |

If you're about to write a paragraph that duplicates one of these, link instead.

## Workflow rules

- **Smoke-test after every slice for WPF work.** `dotnet build` validates XAML compiles — it does not validate binding paths, layout, visibility triggers, or keyboard flow. Launch the exe and exercise the changed feature before claiming done.
- **Launch it with `tools/run-scratch.ps1`, not `dotnet run` or F5, on any PC that also runs the real app.** The script gives the dev build a throwaway PostgreSQL 18 container, empty settings folders, a stand-in Tally on port 9000 and its own API on port 5108, so it cannot reach the saved production server or post test vouchers to TallyPrime. (A dev Desktop started any other way reads this PC's saved server address and would work against production.) See [DEV_SETUP.md](DEV_SETUP.md#run-against-a-scratch-database). A build fails while that app is running (file lock); close it first, or build `-c Release` for the tests.
- **`ViewSmokeTests` is the automated half of that.** It loads every view against the real `MainWindowViewModel` (no window is shown) and fails on a XAML load error or a binding path error, so run the Desktop tests after any XAML change. It cannot see focus or keyboard flow; those still need the exe. A new view is picked up automatically; give it a data context in `DataContextFor` if `MainWindow.xaml` gives it one.
- **To look at the views without launching the app, run it in snapshot mode.** Set `TALLYWRAPPER_VIEW_SNAPSHOTS` to a folder and run `ViewSmokeTests`: each view (and each Settings section) is saved there as a PNG, laid out at 1280×800 with the test's sample data. Use it after any change to `Styles.xaml`, `DesignTokens.xaml` or a view. Hover, press and disabled states of the button styles are covered by `ButtonStyleTests` instead, since a snapshot cannot show them.
- **To look at a printed bill after a layout change,** set `TALLYWRAPPER_PRINT_SAMPLES` to a folder and run `BillDocumentRenderTests`: it keeps `sample_invoice.pdf` and `sample_invoice_overflow.pdf` there.
- **Update docs in the same pass as code.** If a slice changes a setting, endpoint, shortcut, or user-visible flow, edit `docs/` and `DEV_SETUP.md` alongside the code. Don't leave them drifted.
- **Scope discipline.** For refactors or UI audits, prefer the smallest visual/logical change that closes the gap. No pre-emptive abstractions, no unrelated cleanup, no renaming "while you're in there".
- **Ask before rebuilding at scale.** If a request could mean "touch a handful of files" or "rewrite every view", use `AskUserQuestion` before starting. The blast radius matters.
- **Never `--no-verify` or skip pre-commit hooks.** If a hook fails, fix the cause; don't bypass.

## Design system — non-negotiables

The design target is the hi-fi prototype in [docs/design/](docs/design/), distilled into [docs/15_ui_design_reference.md](docs/15_ui_design_reference.md). Tokens live in [Resources/DesignTokens.xaml](src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml); control styles in [Resources/Styles.xaml](src/ShowroomBilling.Desktop/Resources/Styles.xaml).

- **Density tokens are read with `DynamicResource`.** `RowHeight`, `InputHeight`, `ButtonHeight` and the six `FontSize*` keys are overridden at run time by `UiDensityManager`, so a `StaticResource` reference or a literal `10.5` / `11` / `11.5` / `12.5` font size ignores the setting. `UiDensityTests` checks that the manager's Compact values match `DesignTokens.xaml`.
- **One button template.** `BtnBase` owns the template; `BtnPrimary`, `BtnDanger`, `BtnGhost` and `BtnSm` only set brushes, and hover / pressed / disabled are style triggers on `Background` / `BorderBrush` / `Foreground`. So never set those three directly on a `<Button>` in a view (a local value beats the triggers); pick or add a style. Text inside a button, whether plain `Content` or your own `TextBlock`, takes the button's `Foreground`, which is how a primary button gets white text and a disabled one greys out.
- **Always use tokens, never hardcode hex.** If a color isn't in the token sheet, add it to `DesignTokens.xaml` — don't inline `#FF...` in a view.
- **Typography is Windows-native.** `UiFontFamily` → `Segoe UI Variable Display, Segoe UI Variable, Segoe UI`. `MonoFontFamily` → `Cascadia Mono, Consolas`. The design calls for Inter/JetBrains Mono; those aren't installed on Windows so we use the closest native equivalents. Don't reintroduce Inter/JB Mono unless you're also bundling the TTFs.
- **Accent color is `#4F46E5`** (Tailwind indigo-600) — this matches the design bundle's own SVG thumbnail, not the muted oklch conversion we had earlier.
- **Dialog chrome pattern:** the root `Grid` paints `DialogScrimBrush`; the panel is one `Border` with `Style="{StaticResource DialogPanel}"` (centred, with the shared `DialogShadow`), then `dialog-head` with padded border-bottom and `dialog-foot` with `BgSunkenBrush` and `DividerBrush` top border. Do not paste the scrim hex or a `DropShadowEffect` into a dialog. Follow [AdminUnlockDialog.xaml](src/ShowroomBilling.Desktop/Views/Admin/AdminUnlockDialog.xaml) or [SystemHealthDialog.xaml](src/ShowroomBilling.Desktop/Views/SystemHealthDialog.xaml) as the reference.
- **Dialogs are `UserControl` overlays, never `<Window>` modals.** All dialogs are hosted once in [MainWindow.xaml](src/ShowroomBilling.Desktop/MainWindow.xaml) and switched on/off via `MainWindowViewModel.ActiveDialog` (one of the `Shell.ShellDialogs` names, or null) + `DialogVisibilityConverter` with `ConverterParameter={x:Static shell:ShellDialogs.…}`. Use the constants, not the bare strings. For *awaitable* prompts (change-number, reason, admin unlock), use a `TaskCompletionSource<T>` kept on `MainWindowViewModel`: the VM configures the dialog VM, sets `ActiveDialog`, and `await`s the TCS; the dialog's OK/Cancel commands fire a `Closed` event that resolves the TCS. See [MainWindowViewModel.PromptChangeNumberAsync](src/ShowroomBilling.Desktop/ViewModels/MainWindowViewModel.cs) / [`ChangeNumberDialogViewModel`](src/ShowroomBilling.Desktop/ViewModels/Bills/ChangeNumberDialogViewModel.cs) for the canonical pattern. `CloseDialogCommand` (Esc) must resolve any in-flight TCS as "cancel" or the awaiter hangs; `ShellDialogCoordinator.OnActiveDialogChanged` also cancels a pending change-number / reason TCS whenever another dialog replaces it.
- **Yes/no questions go through `ConfirmDialogViewModel`, never `MessageBox.Show` in a view model.** `await ConfirmHandler(new ConfirmRequest(title, message, confirmText, IsWarning: …))`; the shell wires `ConfirmHandler` on `BillsViewModel`, `BillDetailsViewModel` and `ServerSettingsViewModel` to `MainWindowViewModel.ConfirmDialog.ShowAsync`. The overlay has its own layer above `ActiveDialog`, so asking from inside another dialog does not replace it; Esc answers "no". A view model with no handler wired treats the answer as "no". Questions two screens share (repost, void) are worded once in `BillConfirmations`. The only message boxes left are the two start-up failures in `App.xaml.cs` / `MainWindow.xaml.cs`, where there is no shell to host an overlay.
- **A Bills action is one `BillsActionRunner.RunAsync` / `ForEachRowAsync` call**: it owns the busy flag (`BillsViewModel.IsActing`), the status text, the failure text and the reload. Don't write that skeleton out again in a workflow.
- **Three attached behaviours replace per-view code-behind** (all in `Shell/`): `shell:PasswordBinding.Password="{Binding Passcode}"` binds a `PasswordBox` two-way (so clearing the view-model value clears the dots); `shell:FocusOnShown.IsEnabled="True"` focuses a field each time its overlay appears; `shell:Kbd.Hint` is the shortcut chip below. Enter-to-submit on a field is a `KeyBinding` in XAML, not a `KeyDown` handler.
- **Keyboard-hint chips:** on a button write `shell:Kbd.Hint="Ctrl+R"` next to `Content` (the button template draws the chip); anywhere else use `<ContentControl Style="{StaticResource Kbd}" Content="F2" />`, or `KbdOnPrimary` inside a filled button's own content. Don't hand-build the `Border` + mono `TextBlock` again. A button with no shortcut gets no chip.
- **Status chips:** 18 px tall, `radius 2`, uppercase mono text, `Soft`/`Ink` token pair (e.g. `WarnSoftBrush` background + `WarnInkBrush` foreground). Bill state, LOCKED/UNLOCKED, ACTIVE/NONE, PENDING/IN SYNC all use this same pattern.
- **Field labels:** uppercase, `11.5 px`, `InkMutedBrush`, `Medium`. Use the `FieldLabel` style rather than redefining inline.
- **Status dots:** `7×7` (design `.dot`). Timeline/checklist bullets: `12×12` (design `.timeline .bullet`).
- **Banners (warn/err/info):** colored background + matching border + icon + `Ink` variant text. See `AdminUnlockDialog` for the pattern.

## EF migrations

Pass the connection string explicitly when migrating any remote database. The EF tools silently ignore `ASPNETCORE_ENVIRONMENT` and fall back to the localhost default in `appsettings.json`, which either fails loudly or quietly migrates a local database you did not mean to touch:

```powershell
dotnet ef database update --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --connection "<postgres-connection-string>"
```

- **After any model change, regenerate the compiled model** the API runs on, or it runs against a stale model:

  ```powershell
  dotnet ef dbcontext optimize --project src/ShowroomBilling.Infrastructure --startup-project src/ShowroomBilling.Api --output-dir Persistence/CompiledModels --namespace ShowroomBilling.Infrastructure.Persistence.CompiledModels
  ```

  Then delete the generated `ShowroomBillingDbContextAssemblyAttributes.cs`: the compiled model is opted into with `UseModel` in `DependencyInjection`, so design-time tools and the tests' own contexts keep building the model from `OnModelCreating`. `CompiledModelTests` fails when the two drift.
- **Production never migrates itself.** Migrations are applied by hand, as the role that owns the tables, before a new build is started. The server service tries at start-up, but its database role may not alter the schema (SQLSTATE `42501`), and nothing else applies them. A new build on a database without its migrations cannot read or save a bill; this took production down on 10 October 2026, when it showed only as an empty Bills tab. It is now reported: `IDatabaseSchemaState` holds the pending-migration count, `/api/runtime/health` returns it as `pendingMigrationCount`, the Desktop shows `DB BEHIND` with a banner, and the tray says "upgrade needed". Keep that signal working when you touch health or start-up code; `SchemaBehindPostgresTests` replays the outage. Never tell anyone a deployed build will migrate production by itself. The steps are in [docs/11 §11](docs/11_deployment_and_ops.md).
- **A migration that drops data copies it aside first**, because whoever applies it may skip the backup. Follow `20261010124946_DropUnusedColumnsAndIndexes` (`20261010132922_DropDraftEditLeases` is the whole-table version): copy the values into a `dropped_<date>_<table>_<column>` table in the same transaction, make `Down` restore from it, and write every statement conditionally (`IF EXISTS` / `IF NOT EXISTS`), because a migration that fails at start-up leaves the API in limited mode. Add a test that runs it both ways against real rows, like `DropUnusedColumnsMigrationPostgresTests`. **Dropping also makes the release all-at-once:** an older build still maps the column or table, so it fails against the migrated database. Say so in docs/11 §11 when you add one.
- **On a machine that also runs the real app, point the EF tools away from its settings first**, or the design-time host reads the saved production connection: set `SHOWROOM_BILLING_APPDATA` to an empty folder and `SHOWROOM_BILLING_POSTGRES` to a dummy value. Neither `migrations add` nor `dbcontext optimize` connects to a database.
- The production secret is in OpenBao (`kv/Postgres/apps/tally_wrapper/prod`); the API does not read OpenBao itself. Commands: [docs/11](docs/11_deployment_and_ops.md#openbao-production-secret).

## Build & test

```powershell
dotnet restore ShowroomBilling.sln
dotnet build ShowroomBilling.sln
dotnet test --solution ShowroomBilling.sln
```

- **Desktop DLL lock on rebuild:** if the Desktop exe is running, MSBuild fails to copy `ShowroomBilling.Printing.dll` into `bin/Debug/net10.0-windows/`. This isn't a compile error — just close the running app and rebuild.
- **VS Code F5:** the `Desktop + API` compound in `.vscode/launch.json` starts both. The `Desktop` configuration alone needs an API already listening on port 5108; it pins that address, so it does not follow the server address saved on this PC.

## Tally integration

Details: [docs/05](docs/05_tally_integration_contract.md) and [docs/03](docs/03_bill_state_machine.md).

- **Synchronous and operator-initiated.** No queue, no background worker, no retry timer. If an operation isn't user-triggered, it shouldn't be talking to Tally.
- **One operator action makes at most one voucher HTTP attempt.** Safe `ITallyXmlClient` reads retry (Polly, 2 attempts); voucher writes are explicitly excluded. A timeout, transport error or unreadable response after the write began is ambiguous and moves the bill to `reconciliation_required`; explicit Tally `LINEERROR` content moves it to `failed`.
- **`reconciliation_required` is left only through admin Mark as Pushed / Mark as Pending**, after the operator has checked Tally. Retry, edit, renumber, void and delete must not bypass it.
- **Do not wrap the push in a transaction.** `BillPostingWorkflow.PushInternalAsync` claims the bill with a conditional `UPDATE` to `posting` (0 rows affected means another request holds it: stop, with no second Tally call), and that claim is committed before Tally is called on purpose, so a crash leaves a visible `posting` row. `StuckPostingRecoveryHostedService` moves claims older than `AbandonedPostingRecovery.AbandonAfter` (10 min) to `reconciliation_required`, each transition conditional on the same claim. The same goes for `RepostAsync`'s pre-flip save.
- **The only write actions:** Save Bill (Invoice tab) and Push / Retry / Repost / Revise / Void / Edit (Bills tab and Bill Details). Printing never mutates bill state.

## API errors

- **Every error body is RFC 7807 ProblemDetails**, written by `DomainExceptionHandler` (`src/ShowroomBilling.Api/Middleware/`). Its mapping table is the code plus the theory in `DomainExceptionHandlerTests`; a new domain exception gets a line in both. Desktop API clients read `detail` / `title` for non-2xx responses.
- **Throw `RequestValidationException` (Application) for a user-facing input error**, with a message written for the operator and no parameter name. A plain `ArgumentException` that names a parameter gets ` (Parameter 'x')` appended by .NET; the handler strips it, but new code should not rely on that.
- **No per-action `try/catch` around domain exceptions in controllers.** Let them bubble. The only legitimate controller-level error return is result-is-null `NotFound()` for read endpoints.
- **Do not check a `[FromBody]` request for null.** With `[ApiController]` a missing body for a non-nullable parameter is answered with a 400 before the action runs (`MissingBodyContractTests` pins this). Where a body really is optional (push, retry, void, revise, delete, master refresh), the parameter is nullable and the action substitutes a default request.

## Admin and device auth

Endpoints and the throttling numbers: [docs/09](docs/09_api_spec.md).

- **Unlock.** Press `~` (backtick) anywhere outside a text box, with no dialog open, to open `AdminUnlockDialog`. The bare-key bindings use gated `*KeyCommand`s so the character still types and an awaited prompt is never replaced. The token lives in `AdminTokenStore` (30 min TTL) and rides as `X-Admin-Token`. If an admin call is made while locked, the unlock dialog opens and the call goes ahead once it closes unlocked.
- **First passcode.** `AdminUnlockDialog` runs `LoadStatusCommand` on open and shows either the unlock form or a "Set passcode" form (`ShowUnlockForm` / `ShowInitialSetupForm` on `AdminUnlockViewModel`). It is the only in-app place to set the first passcode, because Settings → Admin appears only once unlocked; the server accepts a first passcode only from a Desktop on the server PC itself (loopback). Minimum length is `Contracts.Settings.SettingLimits.MinAdminPasscodeLength`, which both the server and the view model read.
- **Server side.** `AdminAuthenticationHandler` (scheme `AdminToken`) validates the token and endpoints are gated with `[Authorize(Policy = AdminPolicy.PolicyName)]`; downstream code reads the session from the `ClaimsPrincipal`. Unlock and passcode rotation go through `IAdminUnlockThrottle`, which is in-memory and resets on API restart by design.
- **Which policy a new endpoint gets.** A mutating endpoint gets `[Authorize(Policy = DevicePolicy.PolicyName)]`: on a server install that means a caller inside the trusted LAN range; for an API on a developer's own machine it means the `X-Device-Token` file secret, which `DeviceTokenHandler` attaches only when the Desktop's API address is loopback. An admin-only endpoint gets the admin policy **only**; do not stack both. Reads are unauthenticated.
- **An action that needs admin only for some bills** keeps the device policy. Saving an edit to a `posted` bill is the case: `BillsController.UpdateDraft` passes on whether the request carries an admin session, and the workflow throws `AdminSessionRequiredException` (403) when it is needed and missing.

### Hide admin-only controls while locked

A button that will fail auth gives the operator no feedback, so admin-only controls are hidden until unlock.

- `BillsViewModel.IsAdminUnlocked` (raised on `AdminTokenStore.Changed`) hides Delete Selected, Change Bill Number and the admin-only context menu items (Change Bill Number, Mark as Pushed, Mark as Pending, Delete Local).
- Settings → Admin is added to and removed from the sidebar by `SettingsSectionCoordinator.SyncAdminSection`; the Batch Data Scheduler group binds `AdminVm.IsUnlocked`.
- **Edit is the exception**, because it needs admin only for a posted bill. It stays visible; on a posted row it asks for unlock before the bill opens (`BillsRowActionWorkflow.EditRowAsync`), and the Invoice tab asks again at Save if the session has expired since.
- **A secret follows the same rule.** The Desktop holds none today (the database connection lives on the server and is edited from its tray). If one is ever shown, fill it in only while admin is unlocked, clear it on lock, and never replace text the operator is typing.
- **Context menus are popups, a separate visual tree**, so `RelativeSource AncestorType=UserControl` from a `<MenuItem>` finds nothing. The Bills row `Border` sets `Tag="{Binding DataContext, RelativeSource={RelativeSource AncestorType=UserControl}}"` to hand `BillsViewModel` down, and menu items bind `{Binding PlacementTarget.Tag.IsAdminUnlocked, RelativeSource={RelativeSource AncestorType=ContextMenu}}`. Every item on that menu binds its command this way, including Open Details and Print, whose commands the shell hands to `BillsViewModel` (`OpenDetailsCommand`, `PrintSelectedCommand`); do not add `Click` handlers. `ViewSmokeTests` connects each context menu to its owner and fails on an item whose command binding found nothing.
- The `ADMIN` chip in [NavBarView.xaml](src/ShowroomBilling.Desktop/Views/NavBarView.xaml) lights green when unlocked and invokes `LockCommand` on click.

## Bill payload validation

`BillValidator.Validate` (Application) runs on every client-supplied `BillPayloadDto` before it is stored: create, update, and revise when it carries a payload. The rules are listed in [docs/09 §2.2.1](docs/09_api_spec.md). **Do not validate `Subtotal == Σ LineTotal`** or `LineTotal == Rate × Quantity`; jewellery pricing makes both invalid assumptions. Add new invariants to `BillValidator.Validate`, not controllers or services.

## Start-up hosted services

A start-up hosted service must never block or fail the API boot: bound it with a timeout and log failures at warning. One that needs the migrated database derives from `DatabaseStartupTask` (Infrastructure/Persistence): the base waits for `IStartupStatus.WaitForDatabaseReadyAsync`, bounds the work, never throws into the host and drains for 2 s on stop. Stuck-posting recovery and sequence self-heal use it. The wait faults with `DatabaseNotReadyException`; only that type means "database not ready", and any other exception from the work is reported as a failure.

`IStartupStatus` is in-process only. What the Desktop's CLOUD pill shows comes from `/api/health/ready` and `/api/runtime/health?forceDatabase=true`, which report whether the database answers now.

## Audit rows

- **Append-only.** Bill audit rows are never purged during edit-and-repush and are not cascade-deleted with the bill. `BillAuditStore.GetAuditAsync` reads by entity ID even after the bill row is gone, so the `bill.deleted` tombstone and the full prior history stay queryable.
- **Actors** come from `IAuditActorContext`: device-authenticated mutations record `device/desktop`, admin mutations record `admin/<actor>`, and start-up or background work records `system`.
- **Never build audit JSON by string concatenation**; an invoice number or reason containing a quote used to break it silently. Bill events go through `BillAuditStore.Write(billId, eventType, state, at, details)` and every other row through `AuditRow.Create` (Infrastructure/Persistence); both serialize an object. The wire shape is `{ "state": "...", "details": { ... } }` and the Desktop's `BillDetailsViewModel` reads `details.reason` / `details.invoiceNumber` / `details.fiscalYear`; keep that.
- Bill event names are the constants in `Contracts.Bills.BillAuditEvents`; when you add one, add its title to the Desktop's `BillAuditTrailMapper.TitleFor` (a test fails otherwise).

## Transactions

Most commands are a single `SaveChangesAsync`, which EF wraps in an implicit transaction. A few open an explicit one because they make several round trips on the same `ShowroomBillingDbContext`: creating a bill (`BillLifecycleWorkflow.CreateNewDraftAsync`) and revising one (number, bill, revision and audit commit together), `BillAdminWorkflow.DeleteAsync`, and `ChangeInvoiceNumberAsync` (the save plus the sequence reclaim).

`NumberingService.ReserveAsync` enlists in the caller's transaction and leaves the save to the caller, so the number and the bill commit in one batched round trip. **A caller that opens a transaction around `ReserveAsync` must call `SaveChangesAsync` afterwards.** The push is the deliberate exception; see Tally integration above.

## Shared constants and helpers you must use

- **Bill states:** [`ShowroomBilling.Contracts.Bills.BillStates`](src/ShowroomBilling.Contracts/Bills/BillStates.cs) is canonical, including `ReconciliationRequired`. **Do not compare `bill.State` to string literals.**
- **Invoice number formatting:** [`ShowroomBilling.Contracts.Numbering.InvoiceNumberFormatter`](src/ShowroomBilling.Contracts/Numbering/InvoiceNumberFormatter.cs) is the single formatter, used by server-side reservation, change-number auto-format and the Desktop `ChangeNumberDialog` preview. If you need to render or compare an invoice number, go through it; never hand-build `{prefix}{year}/{NNNN}{suffix}`.
- **File locations:** [`ShowroomBilling.Contracts.AppPaths`](src/ShowroomBilling.Contracts/AppPaths.cs) is the only place that knows `%APPDATA%\ShowroomBilling` (`DataDirectory`, `LogsDirectory`) and `%LOCALAPPDATA%\ShowroomBilling` (`LocalDirectory`: device token). Build every path from it; do not call `Environment.GetFolderPath` for an app file. Both test projects redirect it to a scratch folder in a module initializer (`TestSupport/TestAppData.cs`), so a store that bypasses `AppPaths` would make the tests read or overwrite the real settings of whoever runs them, including the saved database connection. `TestIsolationTests` / `AppPathsTests` check the redirect.

## Numbering

How reservation, forward-skip and roll-back behave: [docs/04 §2.4–2.6](docs/04_numbering_and_idempotency.md).

- **Every path that sets `InvoiceNumber` (create, revise, change-number) must also set `InvoiceCore`.** Occupancy is read from the stored core, never from display text, because a suffix such as `-A2` parses as core 2. Rows with a NULL core are legacy custom numbers and fall back to `InvoiceNumberFormatter.TryParseTrailingCore`.
- **A path that removes a bill from the sequence's trailing slot, or moves it, calls `NumberingSequenceReclaimer.ReclaimAsync` inside its transaction**, as delete and change-number do. Do not re-invent the roll-back: it also raises `MinNextValue` past a core whose voucher Tally still holds, so a roll-back never goes back below that number.
- **Change-number accepts digits only** and formats them with `InvoiceNumberFormatter`.
- `invoice_number_reservations` is no longer written or read; the table and its mapping stay until a migration drops them. A repeated Save is recognised by `bills.CreateOperationId`.

## Master data refresh

- **One `Refresh from Tally` button per master section**, not a Fetch and a Refresh. It re-pulls from Tally and reloads the dropdown, and Settings loads any empty snapshot when it opens. Don't reintroduce a separate "Fetch" button; operators forgot the second click.
- Each button names the masters it wants (`SettingsMasterSnapshotWorkflow.RefreshAsync`): the Ledgers button sends `ledgers` and then `voucher-types`. A request with no master type refreshes all four, which is what System Health's "Refresh all" is for.
- **`POST /api/masters/refresh` returns one `TallyMasterRefreshResult` per master fetched.** Don't deserialize it as a single accept-shape record; that once swallowed real failures. `SettingsRefreshSummaryFormatter.Summarize` renders per-master counts, or the first failing master.
- **`TallyMasterRefresher.RefreshAllAsync` runs the four masters sequentially**, each in its own DI scope. Tally's local XML endpoint times out under concurrent reads.

## Tests

Where the test database server comes from: [DEV_SETUP.md](DEV_SETUP.md#database-tests).

- **Every test that touches the database runs on PostgreSQL 18**, the production version. There is no in-memory provider and no code path that exists only for tests.
- **Derive from `DatabaseTest`** to get a fresh copy of the migrated template for each test; `CreateDbContext()` returns a new context on it. A test that needs something else (a database stopped at an older migration, several connections racing) takes `PostgresFixture` in its constructor and asks it directly, as the classes under `Postgres/` do.
- **A test class that needs the database carries `[Trait("Category", "Postgres")]`**: inherited from `DatabaseTest`, set on the class everywhere else. CI runs `Category!=Postgres` on Windows and `Category=Postgres` on Linux, so a class without the trait fails the Windows job, and these tests must stay free of Windows-only assumptions (path separators, DPAPI).
- **Check what was stored with a second context.** `ExecuteUpdateAsync` / `ExecuteDeleteAsync` change rows without touching entities a context already tracks, so reading back through the service's context can show the old values.
- **A new or changed endpoint gets a contract test** in `tests/ShowroomBilling.Tests/Contracts/`. `TestApiFactory` boots the real API in-process on a database of its own, with auto-migrate off and `StubTallyMasterRefresher`; the device token comes from `factory.GetDeviceToken()`. `MasterRefreshContractTests` is the example.

## Things I learned the hard way

- Bool-backed state fields (e.g. `IsLocalDatabaseOverridePresent`) rendering as literal `True` / `False` in XAML is almost always a design bug. Replace with two `Border` chips gated by `BoolToVis` / `InverseBoolToVis`.
- **V1-ported constants are frozen.** The synthetic Batch Data Scheduler partitions bills in `[₹25,000, ₹1,99,000]`; ₹1,99,000 is a GST-compliance cap, not a preference. Do not relax it without product sign-off. See [docs/17_synthetic_batch.md](docs/17_synthetic_batch.md) and `SyntheticBatchPlanLimits`.
- **CommunityToolkit.Mvvm gotcha.** Setting an `[ObservableProperty]` inside a VM constructor triggers its `OnXxxChanged` partial. If that partial calls `SomeCommand.NotifyCanExecuteChanged()` and `SomeCommand` hasn't been assigned yet, you get a `NullReferenceException` in the ctor. Initialise commands *before* setting any observable property that has a partial-method hook.
- **`RelativeSource AncestorType=UserControl` resolves to the nearest UserControl *and reads its current DataContext*** — not the inherited one. If the UserControl has `DataContext="{Binding Foo}"` set on it in the host (e.g. `SettingsView DataContext="{Binding Settings}"` in MainWindow), then `AncestorType=UserControl` hits `SettingsViewModel`, not `MainWindowViewModel`. Silent bugs (missing command on wrong VM) result. Reach for `AncestorType=Window` when you need `MainWindowViewModel` from inside a nested view. Precedent: the `OpenSyntheticBatchCommand` button was silently inert for months because of this.
- **`KeyBinding Key="P"` without a modifier fires even while a TextBox has focus** (plain-letter keys aren't marked Handled by TextBox for routing purposes). Filter/search inputs can trigger parent commands mid-typing. Always pair bare-letter shortcuts with `Ctrl`/`Shift`/`Alt`, or skip the keybinding and rely on a button.
- **Reading `dotnet-trace` for an `async void` entry point is a trap.** `App.OnStartup` shows up with multi-second *inclusive* time in the trace, but that includes the entire async continuation that runs on the dispatcher message-loop *after* `window.Show()` has returned (via `MainWindow.OnLoaded` → `InitializeAsync`). The actual pre-window-paint cost is much smaller — verify with the `[startup-timing]` log line that `App.OnStartup` emits (Information level, written to `%APPDATA%\ShowroomBilling\logs\desktop-*.log`), not by reading inclusive ms off the speedscope flame graph. Phases logged: `hostBuild`, `hostStart`, `resolveWindow`, `showWindow`, `windowVisibleAt` (total).
- **`PublishReadyToRun=true` is set in one place**, `Get-PublishArguments` in `tools/PublishCommon.ps1`, which every publish script uses; change it there if you need a non-R2R build (for example to reproduce a JIT-only bug). It costs about 10 % binary size and 30 s of publish time for roughly 800 ms off a cold launch. The same file's `Remove-PublishFolder` is the only way a publish script deletes a folder, and it refuses anything outside the repo's `publish\` directory.
