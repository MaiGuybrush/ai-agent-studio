## Problem Statement

Enterprise manufacturing applications built from the frontend starter template undergo frequent iterative releases to support new fabrication workflows, API contract updates, and bug fixes. Currently, users and downstream developers face three key gaps:

1. When a new version of an application is deployed, authenticated employees (`EmpInfo`) are unaware of new features, workflow adjustments, or breaking changes because there is no automated release-note notification upon login.
2. Employees who dismiss or miss an initial update announcement have no way within the `AppShell` header to review historical version release notes on demand.
3. The `AppShell` lacks a standardized enterprise footer displaying the active application version, corporate copyright notice, and responsible module owner contact information (`Owner` & `Ext`) resolved from `RuntimeConfig`, making it difficult for factory users to report issues to the right contact person or verify which version they are running.

Furthermore, legacy implementations relied on Angular Material dialog primitives and had a semantic version comparison defect (`compare(m, lastRead) > 1`) that prevented subsequent version updates from popping up once a user had read any prior version. Downstream template consumers need a modern, Material-free UI implementation that preserves structural compatibility with existing `VersionInfo` definitions and Markdown release note conventions.

## Solution

1. Introduce a standalone, Signal-driven `VersionInfoService` and `VersionInfo` data contract that maintains structural compatibility with existing enterprise projects (`versions: string[]` ordered from newest to oldest, and `readCompleteSaveKey: string`).
2. Automatically check for unread versions when an authenticated employee (`EmpInfo`) session becomes active. Using standard semantic version comparison (`compareSemver(version, lastRead) > 0`), if one or more versions are newer than the employee's last acknowledged version in `localStorage`, automatically present a redesigned timeline-card modal dialog displaying the Markdown release notes for those unread versions.
3. Persist the latest version string (`versions[0]`) to `localStorage` under `${user.no || user.id}-${appTitle}-readVersionInfo` only after the user explicitly acknowledges the dialog ("我知道了"), ensuring subsequent logins do not re-prompt until a newer version is deployed.
4. Redesign the `AppShell` top header right-hand action area to use a three-dots dropdown menu containing both "版本資訊" (which opens the full historical version timeline on demand) and "登出" (Logout), while keeping the header left-hand title clean without redundant version badges.
5. Add a three-column enterprise `Footer` component at the bottom of the `AppShell` displaying:
   - Left: Current application version (`versions[0]`).
   - Center: Corporate copyright statement (`© 2026 Innolux Inc. All rights reserved.`).
   - Right: Responsible module owner and extension (`Owner: <owner>(Ext: <extNo>)`) resolved dynamically from `RuntimeConfig` (`modulesInfo`) based on the active route or default module configuration.

## User Stories

1. As an authenticated enterprise employee (`EmpInfo`), I want to automatically see a version update dialog when I log into the application for the first time, so that I am immediately informed of the current release capabilities.
2. As an authenticated enterprise employee (`EmpInfo`), I want the automatic version dialog to appear whenever a version newer than my last acknowledged version is deployed, so that I never miss important feature updates or operational changes.
3. As an authenticated enterprise employee (`EmpInfo`), I want the automatic dialog to show only the versions that are newer than my last acknowledged version, so that I only read relevant new changes rather than scrolling through old history.
4. As an authenticated enterprise employee (`EmpInfo`), I want unread or latest versions in the dialog to be highlighted with a visual `NEW` badge in a timeline card layout, so that I can quickly distinguish new updates at a glance.
5. As an authenticated enterprise employee (`EmpInfo`), I want my acknowledgment ("我知道了") to be saved per employee and per application (`${user.no || user.id}-${appTitle}-readVersionInfo`), so thatonce I confirm the update, the dialog does not pop up again on subsequent page refreshes or logins for the same version.
6. As an authenticated enterprise employee (`EmpInfo`) sharing a workstation with other factory operators, I want version read state to be scoped to my employee identifier (`no` or `id`), so that another operator acknowledging an update does not suppress the notification for my account.
7. As an authenticated enterprise employee (`EmpInfo`), I want to click a three-dots menu button in the right section of the `AppShell` header to access "版本資訊" and "登出", so that header utility actions are organized cleanly and consistently with corporate UAC applications.
8. As an authenticated enterprise employee (`EmpInfo`), I want to select "版本資訊" from the `AppShell` header menu at any time to view the complete history of all versions (`versions`), so that I can review past release notes whenever needed.
9. As an authenticated enterprise employee (`EmpInfo`), I want each version's release notes to be rendered from Markdown with proper formatting (headings, bullet lists, inline code blocks) and HTML sanitization, so that release notes are readable and secure against XSS.
10. As an authenticated enterprise employee (`EmpInfo`), I want to see the current application version in the bottom-left corner of the `AppShell` footer (without duplicating it next to the header title), so that I can always verify the active build version when reporting an issue.
11. As an authenticated enterprise employee (`EmpInfo`), I want to see the corporate copyright notice (`© 2026 Innolux Inc. All rights reserved.`) centered in the `AppShell` footer, so that the application adheres to corporate branding standards.
12. As an authenticated enterprise employee (`EmpInfo`), I want to see the responsible system owner and extension number (`Owner: <owner>(Ext: <extNo>)`) in the bottom-right corner of the `AppShell` footer, so that I know whom to contact for support on the current module.
13. As a downstream developer using `Scaffold`, I want the `VersionInfo` data structure (`versions: string[]`, `readCompleteSaveKey: string`) and `VERSION_INFO` injection token to match the existing corporate convention, so that migrating or maintaining version lists across projects requires zero structural changes.
14. As a downstream developer, I want to add a new version simply by prepending a version string (e.g., `'1.1.0'`) to `versions` and placing a Markdown file under the version assets directory, so that publishing release notes requires no component code changes.
15. As a deployment engineer, I want to configure `modulesInfo` (containing `moduleName`, `version`, `owner`, `extNo`, and optional route matching) in `RuntimeConfig`, so that owner contact information in the footer can be updated without rebuilding the application.
16. As a frontend developer, I want the version dialog, header dropdown menu, and footer to be implemented using native Angular Signals and Tailwind CSS / standard CSS without Angular Material dependencies, so that the template remains lightweight and aligned with the modern tech stack.
17. As a QA automation engineer, I want the entire version notification, Markdown fetching, header menu interaction, and footer rendering workflow to be testable through a single high-level `AppShell` seam using mocked HTTP responses and `localStorage`, so that tests remain resilient to internal refactoring.

## Implementation Decisions

1. **VersionInfo Data Contract & Injection Token**:
   - Maintain exact structural parity with the corporate `VersionInfo` contract:
     ```ts
     export interface VersionInfo {
       versions: string[];
       readCompleteSaveKey: string;
     }
     ```
   - Provide `versionInfo` (initialized with `versions: ['1.0.0']` and `readCompleteSaveKey: ''`) via an `InjectionToken<VersionInfo>('VERSION_INFO')` registered in the application root configuration.

2. **Markdown Asset Convention & Safe Rendering**:
   - Each version string `<version>` in `VersionInfo.versions` maps to a static Markdown file fetched via `HttpClient` at `./assets/versions/<version>/version-<version>.md` (with an initial sample file provided for version `1.0.0`).
   - Markdown content is parsed into HTML using `marked` and sanitized with `DOMPurify` prior to rendering to guarantee XSS safety.

3. **Signal-Based `VersionInfoService` & Semantic Version Comparison**:
   - `VersionInfoService` manages modal visibility and active version list state using Angular Signals (replacing `MatDialog`).
   - Exposes `showVersionInfoDialog(showAll: boolean = false)`:
     - Reads `lastRead` from `localStorage.getItem(versionInfo.readCompleteSaveKey)`.
     - Computes unread versions using a pure semantic version comparator `compareSemver(v, lastRead) > 0` (fixing the legacy `> 1` defect). If `lastRead` is absent, all versions in `versionInfo.versions` are considered unread.
     - When `showAll` is `false` (auto-check on login), opens the dialog displaying only the unread versions. If there are no unread versions, the dialog does not open.
     - When `showAll` is `true` (triggered manually from the Header menu), opens the dialog displaying all versions in `versionInfo.versions`, while still tracking which versions are latest/unread so visual `NEW` badges can be displayed appropriately.
   - Exposes an acknowledgment action (`acknowledgeVersionInfo()` / dialog close confirmation) that writes `versionInfo.versions[0]` to `localStorage.setItem(versionInfo.readCompleteSaveKey, versionInfo.versions[0])` when `readCompleteSaveKey` is non-empty and closes the dialog.

4. **Automatic Trigger on `EmpInfo` Session Activation**:
   - When `AuthService.currentUser` emits an authenticated `EmpInfo` (via an Angular `effect()` in `AppShell`), set `versionInfo.readCompleteSaveKey = "${user.no || user.id}-${configService.appTitle()}-readVersionInfo"` and invoke `versionInfoService.showVersionInfoDialog(false)`.

5. **Redesigned Timeline Version Dialog UI**:
   - A standalone, Material-free modal overlay rendered within `AppShell` when `VersionInfoService` dialog state is active.
   - Features a vertical timeline card list where each version card displays its version header, a `NEW` / `最新` badge (for the latest version or unread versions), and the sanitized Markdown release notes.
   - Includes a footer bar with the primary confirmation button ("我知道了") that triggers version acknowledgment.

6. **Header Three-Dots Dropdown Menu (`AppShell`)**:
   - The `AppShell` header left area displays only the sidebar toggle button and `appTitle` (no version badge in the header).
   - The `AppShell` header right area displays the authenticated `EmpInfo` summary badge (`name`, `id`, `ShopContext`) followed by a three-dots icon button (`more_vert` style).
   - Clicking the three-dots button toggles a floating dropdown menu with two actions:
     - **版本資訊**: Calls `versionInfoService.showVersionInfoDialog(true)` and closes the dropdown menu.
     - **登出**: Calls `authService.logOutObs()` and closes the dropdown menu.
   - Clicking outside the menu or selecting an item closes the dropdown menu.

7. **Enterprise Footer & `RuntimeConfig` Module Owner Resolution**:
   - Extend `RuntimeConfig` with an optional `modulesInfo` array:
     ```ts
     export interface ModuleInfoConfig {
       moduleName: string;
       version?: string;
       owner: string;
       extNo: string;
       routePattern?: string;
     }
     ```
   - Provide default `modulesInfo` entry in `RuntimeConfig` (`assets/config.json`) so starter projects display owner metadata out of the box.
   - Create a standalone `FooterComponent` embedded at the bottom of `AppShell` with three sections:
     - Left (`bottom-front`): Displays `versionInfo.versions[0]`.
     - Center (`bottom-center`): Displays `© 2026 Innolux Inc. All rights reserved.`.
     - Right (`bottom-end`): Observes router navigation events (`NavigationEnd`) and `ConfigService.config()` to match the active URL against `modulesInfo` (falling back to the first `modulesInfo` entry if no route-specific pattern is configured), rendering `Owner: ${owner}(Ext: ${extNo})`.

## Testing Decisions

- **Single High-Level Seam: `AppShell` Integration Boundary (`HttpTestingController` + `localStorage`)**:
  - Tests will exercise the entire feature through `AppShellComponent` wired with the real `VersionInfoService`, `ConfigService`, `Router`, browser `localStorage`, and Angular's `HttpTestingController`.
  - Good tests verify observable external behavior rather than internal Signals or private methods:
    1. **First-time login auto-popup**: With empty `localStorage` and an authenticated `EmpInfo`, `AppShell` automatically opens the version dialog, fetches `./assets/versions/1.0.0/version-1.0.0.md` via HTTP, and renders the Markdown content inside the timeline card.
    2. **Acknowledgment persistence**: Clicking "我知道了" closes the dialog and writes `'1.0.0'` to `localStorage` under `${user.no}-${appTitle}-readVersionInfo`.
    3. **Suppression when up-to-date**: When `localStorage` already stores `'1.0.0'` (and `versions` is `['1.0.0']`), rendering `AppShell` does not open the dialog.
    4. **Multi-version upgrade filtering**: When `localStorage` stores `'1.0.0'` and `versions` is `['1.2.0', '1.1.0', '1.0.0']`, logging in automatically opens the dialog and fetches only `1.2.0` and `1.1.0` Markdown files (verifying the fix for `compareSemver > 0`).
    5. **Manual history inspection via Header three-dots menu**: Clicking the header three-dots button opens the dropdown menu; clicking "版本資訊" opens the dialog and loads all versions (`1.2.0`, `1.1.0`, `1.0.0`) even when `'1.2.0'` was already marked read.
    6. **Footer rendering**: Verifies that `AppShell` footer renders the latest version (`1.0.0`) on the left, `© 2026 Innolux Inc. All rights reserved.` in the center, and `Owner: <owner>(Ext: <extNo>)` from `RuntimeConfig.modulesInfo` on the right, and that the header title area does not duplicate the version number.
- **Prior Art**:
  - Existing specs `app-shell.component.spec.ts` and `config.service.spec.ts` establish the baseline patterns for `TestBed`, `HttpTestingController`, and Signal-based `AuthService` / `ConfigService` testing.

## Out of Scope

- Modifying the external `@inx/mat-ui-lib` package repository.
- Server-side persistence of read version state (state remains persisted in browser `localStorage` per corporate `VersionInfoService` convention).
- Editing or authoring release notes from within the web UI.
- Installing or introducing Angular Material (`@angular/material`) into the template repository.

## Further Notes

- Uses domain terminology defined in `GLOSSARY.md` (`EmpInfo`, `ShopContext`, `RuntimeConfig`, `AppShell`, `NavItem`, `MockAuth`, `Scaffold`).
- Complies with `ADR-0002` by sourcing `modulesInfo` owner metadata through `assets/config.json` (`RuntimeConfig`).
