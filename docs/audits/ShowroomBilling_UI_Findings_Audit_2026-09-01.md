# ShowroomBilling UI Findings Audit

**Scope:** WPF operator desktop UI and WinForms server/API companion UI  
**Audit date:** 1 September 2026  
**Method:** Runtime walkthrough, RDP inspection, source trace, and test review  
**Status:** Findings validated; no application source was changed during either audit

## Executive conclusion

The visual foundation is coherent, but the highest-value work is functional. Four critical findings can cause unintended Tally traffic, inconsistent print behavior, incomplete bill results, or an unreachable workstation URL. Correctness and operator trust should precede cosmetic refinement.

This audit contains 16 findings:

- **P0 - Critical:** 4 findings that can break a core workflow or violate an operational contract.
- **P1 - High:** 6 findings that materially affect reliability, clarity, accessibility, or operator trust.
- **P2 - Planned:** 6 improvements for maintainability, safe maintenance, diagnostics, and regression coverage.

## Combined priority register

| ID | Priority | Area | Finding |
|---|---|---|---|
| D-01 | P0 | Desktop | Passive health checks contact Tally |
| D-02 | P0 | Desktop | Print Estimate button and F9 use different flows |
| D-03 | P0 | Desktop | Posted-day filtering is page-local |
| D-04 | P1 | Desktop | Maximized window can extend below the taskbar |
| D-05 | P1 | Desktop | Shortcut help is not contextual or truthful |
| D-06 | P1 | Desktop | Settings editing and Ctrl+S are split across models |
| D-07 | P1 | Desktop | Keyboard focus and accessibility semantics are incomplete |
| D-08 | P1 | Desktop | Invoice grid cannot fit the normal window |
| D-09 | P2 | Desktop | Design-token and state-constant drift remains |
| S-01 | P0 | Server UI | Workstation URL selects a link-local adapter |
| S-02 | P1 | Server UI | Database card presents a skipped check as `idle` |
| S-03 | P2 | Server UI | Default-size layout hides Copy Server URL |
| S-04 | P2 | Server UI | Service and install actions block the UI thread |
| S-05 | P2 | Server UI | Database maintenance lacks current-state context and safeguards |
| S-06 | P2 | Server UI | Operator diagnostics are raw and difficult to scan |
| S-07 | P2 | Server UI | Server-tray behavior has no automated coverage |

## Recommended implementation sequence

1. **Correctness and reachability:** Resolve D-01, D-02, D-03, S-01, and S-02 first. These can contact Tally unexpectedly, misroute printing, hide bills, advertise an unreachable address, or misstate database health.
2. **Core operator workflow:** Address D-04 through D-08 and S-03 through S-06. This restores reliable window sizing, keyboard behavior, Settings saving, accessibility, data-entry fit, responsive maintenance actions, and clear diagnostics.
3. **Regression hardening:** Complete D-09 and S-07, update documentation in the same pass, add focused tests, and smoke-test both applications at supported sizes and DPI settings.

---

## Desktop application findings

### D-01 - Passive health checks contact Tally

**Priority:** P0 - Critical

**Finding:** Startup and periodic shell-health work still trigger Tally calls. This conflicts with the operator-initiated integration contract, under which Tally should be contacted only by an explicit Push, Refresh from Tally, or operator-requested health action.

**Impact:** A passive UI concern can create avoidable timeouts, noise, and load against Tally's fragile local XML endpoint. It also makes the documented responsibility split unreliable.

**Recommendation:** Keep passive health neutral or cached. Move live Tally probing behind an explicit health refresh while retaining the existing operator-triggered push and master-refresh paths.

**Evidence:**

- [`MainWindowViewModel.cs`](../../src/ShowroomBilling.Desktop/ViewModels/MainWindowViewModel.cs) around line 520
- [`ShellHealthCoordinator.cs`](../../src/ShowroomBilling.Desktop/ViewModels/ShellHealthCoordinator.cs) around line 184

**Acceptance criteria:**

- Application startup and the recurring health timer make no Tally XML request.
- An explicit operator health refresh still reports current Tally state.

### D-02 - Print Estimate button and F9 use different flows

**Priority:** P0 - Critical

**Finding:** The Invoice screen's Print Estimate button binds to a status-only command, while F9 routes through the real print-preview coordinator. The visible button therefore does not open the preview that the keyboard command opens.

**Impact:** Operators receive inconsistent behavior for the same advertised action and may conclude that printing is broken. Validation can also diverge between the mouse and keyboard paths.

**Recommendation:** Bind the button and F9 to one shell-level preview command and centralize empty-invoice validation and error reporting.

**Evidence:**

- [`InvoiceView.xaml`](../../src/ShowroomBilling.Desktop/Views/Invoice/InvoiceView.xaml) around line 574
- [`InvoiceViewModel.cs`](../../src/ShowroomBilling.Desktop/ViewModels/Invoice/InvoiceViewModel.cs) around line 731

**Acceptance criteria:**

- The button and F9 open the same preview dialog for the same invoice state.
- Both paths show the same actionable validation when no printable lines exist.

### D-03 - Posted-day filtering is page-local

**Priority:** P0 - Critical

**Finding:** Bills are fetched in 50-row pages and posted-day filtering is then applied only to the current page. This can remove every row on a page even though matching bills exist elsewhere and makes the visible count inconsistent with the server result set.

**Impact:** Operators can see empty pages, misleading totals, and incomplete date-group results. The problem grows with bill volume.

**Recommendation:** Move filtering and grouping semantics into the API query layer or paginate by complete date groups. Return counts for the filtered result set rather than the pre-filtered page.

**Evidence:**

- [`BillsViewModel.cs`](../../src/ShowroomBilling.Desktop/ViewModels/Bills/BillsViewModel.cs) around line 445

**Acceptance criteria:**

- A date group is either fully present or fully excluded.
- Filtering cannot create a false empty page, and totals match visible navigation.

### D-04 - Maximized window can extend below the taskbar

**Priority:** P1 - High

**Finding:** The maximized window uses a padding compensation rather than per-monitor work-area sizing. During runtime review, the F-key and status strips rendered beneath the Windows taskbar.

**Impact:** Important navigation and system-state information becomes partially inaccessible, especially on multi-monitor or non-default DPI configurations.

**Recommendation:** Handle `WM_GETMINMAXINFO` and use the current monitor's working area instead of a fixed padding workaround. Verify taskbars on every edge and mixed-DPI monitor transitions.

**Evidence:**

- [`MainWindow.xaml.cs`](../../src/ShowroomBilling.Desktop/MainWindow.xaml.cs) around line 40

**Acceptance criteria:**

- All bottom chrome remains above the taskbar when maximized on each monitor.
- The window recalculates correctly after monitor and DPI changes.

### D-05 - Shortcut help is not contextual or truthful

**Priority:** P1 - High

**Finding:** The shortcut dialog is hardcoded around navigation and Invoice commands. On Bills, one advertised action is literally named `Button`, while important Bills actions do not have a clear keyboard mapping.

**Impact:** Keyboard-heavy operators cannot trust the help surface, and advertised shortcuts diverge from the commands available on the active screen.

**Recommendation:** Generate shortcut help from the active screen's command model. Give Bills actions stable names and assign a deliberate shortcut to Push.

**Evidence:**

- [`ShortcutsDialog.xaml`](../../src/ShowroomBilling.Desktop/Views/ShortcutsDialog.xaml) around line 114
- [`FKeyStripViewModel.cs`](../../src/ShowroomBilling.Desktop/ViewModels/FKeyStripViewModel.cs) around line 38

**Acceptance criteria:**

- The shortcut dialog changes with the active navigation surface.
- Every listed shortcut has a real command, meaningful label, and matching key binding.

### D-06 - Settings editing and Ctrl+S are split across models

**Priority:** P1 - High

**Finding:** Ctrl+S invokes the general Settings `SaveAllCommand`, which depends on the general edit session. Print Layout remains separately editable and has its own Save command, so the standard shortcut does not save that section.

**Impact:** Operators can make visible changes and reasonably believe Ctrl+S saved them when it did not. Dirty-state and discard behavior are inconsistent across sections.

**Recommendation:** Route Save based on the selected settings section, or unify sections under one edit-session model. Add explicit dirty, save, cancel, and navigation-away behavior for Print Layout.

**Evidence:**

- [`MainWindowViewModel.cs`](../../src/ShowroomBilling.Desktop/ViewModels/MainWindowViewModel.cs) around line 465
- [`PrintLayoutSettingsSectionView.xaml`](../../src/ShowroomBilling.Desktop/Views/Settings/PrintLayoutSettingsSectionView.xaml) around line 39

**Acceptance criteria:**

- Ctrl+S saves the active editable settings section.
- Unsaved Print Layout changes trigger the same discard protection as other settings.

### D-07 - Keyboard focus and accessibility semantics are incomplete

**Priority:** P1 - High

**Finding:** Several fields surface to accessibility tools only as unnamed edit or combo-box controls. Navigation styles remove focus visuals without a replacement, and modal focus trapping and restoration are incomplete.

**Impact:** Keyboard and assistive-technology users can lose their position, miss errors, or tab behind a modal. The current UI does not meet the design reference's focus-visible intent.

**Recommendation:** Add `AutomationProperties` names and label relationships, visible focus states, live error announcements, dialog focus trapping, and focus restoration to the invoking control.

**Evidence:**

- [`NavBarView.xaml`](../../src/ShowroomBilling.Desktop/Views/NavBarView.xaml) around line 20
- [`SettingsViewResources.xaml`](../../src/ShowroomBilling.Desktop/Views/Settings/SettingsViewResources.xaml) around line 79

**Acceptance criteria:**

- Every interactive field has a meaningful accessible name and visible keyboard focus.
- Overlay dialogs contain focus and restore it to the invoker on every close path.

### D-08 - Invoice grid cannot fit the normal window

**Priority:** P1 - High

**Finding:** The invoice table enforces a 1200-pixel minimum while Quick Add consumes roughly 260 pixels. At the normal 1180-pixel window width, pricing and total columns require horizontal scrolling.

**Impact:** Core entry fields and Line Total are not simultaneously visible during routine billing, increasing eye movement and the chance of unnoticed pricing errors.

**Recommendation:** Reduce non-critical column widths, make Quick Add narrower or collapsible, and keep the key pricing and Line Total columns visible without horizontal scrolling at the supported normal size.

**Evidence:**

- [`InvoiceView.xaml`](../../src/ShowroomBilling.Desktop/Views/Invoice/InvoiceView.xaml) around line 273

**Acceptance criteria:**

- Critical invoice-entry and total columns fit at the normal 1180-pixel window size.
- Narrow layouts degrade deliberately through collapse or prioritization rather than accidental clipping.

### D-09 - Design-token and state-constant drift remains

**Priority:** P2 - Planned

**Finding:** The UI design reference still describes an older accent representation while the canonical runtime token is `#4F46E5`. Some XAML colors remain hardcoded, and bill-state triggers repeat string literals.

**Impact:** Documentation, implementation, and future UI work can slowly diverge, making visual changes and state additions more error-prone.

**Recommendation:** Make `#4F46E5` authoritative in the design reference, replace remaining inline colors with tokens, and bind XAML state comparisons to shared `BillStates` constants where practical.

**Evidence:**

- [`docs/15_ui_design_reference.md`](../15_ui_design_reference.md) around line 26
- [`DesignTokens.xaml`](../../src/ShowroomBilling.Desktop/Resources/DesignTokens.xaml) around line 6

**Acceptance criteria:**

- Design documentation and runtime tokens name the same canonical accent value.
- New colors and bill states can be changed centrally without hunting view-specific literals.

---

## Server and API companion findings

### S-01 - Workstation URL selects a link-local adapter

**Priority:** P0 - Critical

**Finding:** The live server dashboard advertised `http://169.254.83.107:5107` while the active workstation was `192.168.1.51`. The helper returns the first active non-loopback IPv4 address, including APIPA, tunnel, and virtual-adapter addresses.

**Impact:** Copy Server URL can hand operators an address that workstations cannot reach, blocking onboarding or causing support incidents while the API itself is healthy.

**Recommendation:** Exclude link-local and unsuitable adapters, prefer an interface matching the configured trusted LAN CIDR and default gateway, and provide an explicit persisted adapter/IP choice when selection is ambiguous.

**Evidence:**

- [`ServerUrlHelper.cs`](../../src/ShowroomBilling.ServerTray/ServerUrlHelper.cs) lines 34-49

**Acceptance criteria:**

- `169.254.0.0/16` is never advertised as a workstation URL.
- The selected address matches the trusted LAN or is explicitly chosen and persisted by the operator.

### S-02 - Database card presents a skipped check as `idle`

**Priority:** P1 - High

**Finding:** Dashboard refresh calls the cheap runtime-health endpoint without `forceDatabase=true`. The normal `DatabaseHealthSkipped` result is mapped to `idle`, so the card never verifies PostgreSQL reachability.

**Impact:** Operators can read `idle` as a healthy but inactive database even though no database check occurred. This conflicts with the documented tray feature of showing database health.

**Recommendation:** Use a forced or readiness check for manual refresh and a slower cached cadence for background refresh. When a check is skipped, display `Not checked` plus the last verified time.

**Evidence:**

- [`StatusForm.cs`](../../src/ShowroomBilling.ServerTray/StatusForm.cs) lines 268-294
- [`RuntimeController.cs`](../../src/ShowroomBilling.Api/Controllers/RuntimeController.cs) lines 237-262
- [`docs/11_deployment_and_ops.md`](../11_deployment_and_ops.md) lines 126-136

**Acceptance criteria:**

- The card distinguishes verified ready, verified not ready, and not checked.
- Manual Refresh obtains current database reachability and records when it was checked.

### S-03 - Default-size layout hides Copy Server URL

**Priority:** P2 - Planned

**Finding:** At the configured 920 x 720 default size, the sixth fixed-width server action was not visible. Maximizing the same live window revealed Copy Server URL.

**Impact:** A documented onboarding action disappears in the default window, and the layout gives no indication that more controls are clipped or wrapped out of view.

**Recommendation:** Replace the fixed-width wrapped flow with an explicit adaptive grid or two-row action layout. Collapse status cards to 2 x 2 at narrow widths and verify minimum, default, and maximized sizes.

**Evidence:**

- [`StatusForm.cs`](../../src/ShowroomBilling.ServerTray/StatusForm.cs) lines 62-65, 175-181, and 584-630

**Acceptance criteria:**

- All six server actions are visible at the default and minimum supported sizes.
- Resizing does not clip buttons, status text, or the tray-shutdown action.

### S-04 - Service and install actions block the UI thread

**Priority:** P2 - Planned

**Finding:** Start, stop, and restart wait synchronously for Windows Service transitions for up to 20 seconds. Install/repair synchronously waits for the elevated child process and can wait up to 30 seconds on service transitions.

**Impact:** The dashboard can appear frozen during routine maintenance and offers no progress, cancellation, or protection against conflicting clicks.

**Recommendation:** Run long operations asynchronously, disable conflicting controls, show inline progress and results, and confirm Stop or Restart when active workstation clients would be disconnected.

**Evidence:**

- [`ServerTrayActions.cs`](../../src/ShowroomBilling.ServerTray/ServerTrayActions.cs) lines 73-95
- [`ServerInstaller.cs`](../../src/ShowroomBilling.ServerTray/ServerInstaller.cs) lines 249-299

**Acceptance criteria:**

- The window remains responsive throughout service and installer operations.
- Only valid actions are enabled for the current state, and active-client impact is confirmed.

### S-05 - Database maintenance lacks current-state context and safeguards

**Priority:** P2 - Planned

**Finding:** The connection-string field is blank with no indication of the masked active configuration, environment, storage protection, or pending-restart state. Test and Save remain available without a successful test.

**Impact:** The blank editor looks broken and encourages high-risk configuration changes without enough context. The duplicate restart control also leaves the intended sequence unclear.

**Recommendation:** Load masked metadata from `GET /api/runtime/database`, label the editor `New connection string`, disable empty actions, require a successful test before Save, and offer one contextual restart after saving.

**Evidence:**

- [`StatusForm.cs`](../../src/ShowroomBilling.ServerTray/StatusForm.cs) lines 186-202
- [`RuntimeController.cs`](../../src/ShowroomBilling.Api/Controllers/RuntimeController.cs) lines 299-313

**Acceptance criteria:**

- The active configuration is represented without revealing its password.
- Save cannot run until a non-empty candidate has passed the current validation test.

### S-06 - Operator diagnostics are raw and difficult to scan

**Priority:** P2 - Planned

**Finding:** Connected clients are rendered as pipe-delimited strings even though the contract includes app version, first and last seen, and expiry. Open Local Health launches Chrome on a raw JSON payload.

**Impact:** Support staff must decode low-level text, cannot quickly spot stale or mismatched clients, and leave the dashboard for information that could be presented directly.

**Recommendation:** Use a structured client grid with counter, device, user, mode, address, version, and relative last-seen columns. Add last-refresh time and format health details in-app, or rename the action `Open raw health JSON`.

**Evidence:**

- [`StatusForm.cs`](../../src/ShowroomBilling.ServerTray/StatusForm.cs) lines 303-327
- [`ClientPresenceContracts.cs`](../../src/ShowroomBilling.Contracts/Clients/ClientPresenceContracts.cs) lines 11-25
- [`ServerTrayActions.cs`](../../src/ShowroomBilling.ServerTray/ServerTrayActions.cs) line 58

**Acceptance criteria:**

- Client state can be scanned without interpreting delimiter order.
- Health information is clearly labeled, timestamped, and copyable for support.

### S-07 - Server-tray behavior has no automated coverage

**Priority:** P2 - Planned

**Finding:** The test projects contain no references to `StatusForm`, `ServerUrlHelper`, `ServerTrayActions`, `TrayApplicationContext`, or `ServerInstaller`. High-impact adapter-selection and state-mapping logic therefore regress without a focused test signal.

**Impact:** The APIPA URL defect, skipped-database mapping, button-state rules, and client display semantics can recur even when the main application test suite remains green.

**Recommendation:** Extract a testable presenter or state mapper and inject network candidates and service state. Add unit tests for address selection, database-state mapping, action enablement, and client aging, plus a visual smoke checklist.

**Acceptance criteria:**

- A test reproduces and prevents APIPA-first address selection.
- Status and enablement matrices are covered independently of live Windows services.

---

## Verification and audit boundaries

- The running WPF application was exercised and traced to the current implementation.
- `ShowroomBilling.sln` built successfully, and all 109 desktop tests passed during the desktop audit.
- The live RDP server dashboard was inspected at default and maximized sizes.
- Open Local Health was opened and confirmed to display raw JSON.
- Service, database, installer, shutdown, and other mutating server-dashboard actions were not invoked.
- The build reported one unrelated high-severity `SSH.NET` package advisory outside this UI audit.
- No application source files were modified while producing either audit.

## Completion definition

A finding is complete only when its implementation, focused tests, user-facing documentation, and runtime smoke test agree. WPF slices must be launched after build; server-dashboard slices should be checked at minimum, default, and maximized sizes without unintentionally exercising destructive actions.
