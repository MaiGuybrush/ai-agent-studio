## Problem Statement

Enterprise manufacturing applications are deployed across diverse fabrication sites, reverse proxy architectures, and intranet domains. Developers and deployment engineers face three distinct issues:
1. When configuring `assets/config.json`, the authentication endpoint (`authApiUrl`) often resides on the same web server behind an API Gateway or reverse proxy (e.g., `"/Apigateway/MesApiCore"` or `"/uacapi/adAuth"`). Current implementations fail or produce malformed URLs if `authApiUrl` is not provided as an absolute URL (`http://...`).
2. Developers working in local offline environments (or disconnected from corporate intranet VPN) cannot run or test the frontend because missing network connectivity to the authentication server blocks application bootstrap.
3. The frontend starter template lacks a standardized corporate application layout (AppShell) to display authenticated employee status (`EmpInfo`), navigation items (`NavItem`), and protected route handling (`AuthGuard`).

## Solution

1. Provide an intelligent `RuntimeConfig` URL normalization engine within the authentication subsystem. It transparently handles both same-origin relative paths (e.g., `"/Apigateway/MesApiCore"`) and absolute URLs (`"http://..."`), stripping trailing slashes and ensuring compliant URL construction for both HTTP API requests and SSO browser redirects.
2. Introduce a configurable `MockAuth` mechanism in `RuntimeConfig`. When enabled during local development, the bootstrap sequence bypasses remote authentication requests and immediately populates a synthetic `EmpInfo` session.
3. Establish a standard, responsive corporate `AppShell` with data-driven navigation (`NavItem[]`), user status display, and protected default routing (`/dashboard`).

## User Stories

1. As a deployment engineer, I want to specify `"authApiUrl": "/Apigateway/MesApiCore"` in `assets/config.json`, so that the frontend interacts with the co-located API Gateway without hardcoding hostnames or protocol schemes.
2. As a deployment engineer, I want to specify `"authApiUrl": "/uacapi/adAuth"`, so that different reverse proxy paths on different intranet servers are supported without changing TypeScript code.
3. As a deployment engineer, I want to specify an absolute URL (e.g., `"http://tncimweb1.cminl.oa/uacapi/adAuth"`), so that multi-site cross-origin deployments continue to function seamlessly.
4. As a deployment engineer, I want trailing slashes in `authApiUrl` (e.g., `"/Apigateway/MesApiCore/"`) to be automatically sanitized, so that subsequent endpoints like `/VerifyCert` do not produce malformed double slashes (`//VerifyCert`).
5. As a frontend developer, I want to toggle `"mockAuth": true` in `assets/config.json` during local offline development, so that I can develop and preview UI components without an intranet VPN connection.
6. As a frontend developer, I want `MockAuth` to load a realistic `mockUser` object containing employee ID, name, email, and accessible manufacturing shops, so that my permission-aware components behave realistically.
7. As an enterprise employee arriving with a `CertificateKey` parameter in the URL, I want the application to automatically verify my credential against the configured relative or absolute auth endpoint and strip the key from the URL upon success, so that my browser history remains clean.
8. As an enterprise employee returning to an existing session, I want `APP_INITIALIZER` to restore my authentication status using `RefreshToken`, so that I do not need to re-login every time I refresh the page.
9. As an enterprise employee whose access token expires while interacting with the application, I want `JwtInterceptor` to silently refresh the token via `${authApiUrl}/RefreshToken` and replay my request, so that my operational workflow is uninterrupted.
10. As an enterprise employee whose session is completely invalid or expired, I want to be redirected to the corporate SSO login portal, so that I can re-authenticate.
11. As an enterprise employee, I want to click Logout and have my local session cleared before being redirected to the corporate SSO logout page, so that my account credentials are not left accessible on shared factory workstations.
12. As an enterprise employee, I want to see my employee ID, display name, and active `ShopContext` in the `AppShell` top header, so that I am always aware of my active operational identity.
13. As an enterprise employee, I want to view a left-hand navigation sidebar driven by structured `NavItem` definitions, so that I can easily discover and switch between operational views.
14. As an enterprise employee, I want to collapse or expand the `AppShell` sidebar, so that I can maximize horizontal screen space when viewing high-density operational data tables.
15. As an enterprise employee navigating to the root path (`/`), I want to be automatically redirected to `/dashboard`, so that I land directly on the central operational workspace.
16. As a frontend developer building on this template, I want protected routes to be safeguarded by `AuthGuard`, so that unauthenticated users cannot access restricted business views.
17. As a downstream developer, I want to define navigation menus by adding objects to a `NavItem[]` array rather than modifying HTML markup, so that menus can be extended with minimal effort.
18. As a QA automation engineer, I want all configuration, authentication, and layout logic to be testable with mocked HTTP responses without running a live backend server, so that CI pipelines execute quickly and deterministically.

## Implementation Decisions

1. **RuntimeConfig Model & Normalization**:
   - The runtime configuration schema supports `appTitle` (string), `authApiUrl` (string), `mockAuth` (boolean), and optional `mockUser` (`EmpInfo`).
   - A URL normalization utility standardizes `authApiUrl`:
     - Trims whitespace and trailing slashes.
     - Preserves relative paths (e.g., `"/Apigateway/MesApiCore"` remains relative for Angular `HttpClient` calls, resolving against the hosting origin).
     - For browser redirects (`window.location.href`), if the auth server returns a relative SSO URL or if constructed locally, it is resolved against `window.location.origin`.

2. **Chained Bootstrap Sequence**:
   - `provideAppInitializer` executes the startup lifecycle:
     1. `ConfigService.loadConfig()` fetches `assets/config.json`.
     2. `AuthService.init()` checks `mockAuth`. If enabled, it initializes state with `mockUser` and resolves immediately.
     3. If `mockAuth` is disabled, it inspects URL for `CertificateKey` (executing `verifyCertificate`) or attempts session restoration with `RefreshToken` (`tryAuth`).

3. **Signal-based State Architecture**:
   - `AuthService` exposes `currentUser: Signal<EmpInfo | null>` and `isAuthenticated: Signal<boolean>`.
   - `AppShell` components bind directly to these Signals, eliminating RxJS subscription boilerplate.

4. **Transparent HTTP Interceptor**:
   - `JwtInterceptor` attaches `Bearer {token}` to outgoing API requests (excluding public auth endpoints).
   - Catches 401 Unauthorized responses, queues pending requests, requests a fresh token from `${authApiUrl}/RefreshToken`, and replays queued requests.

5. **Corporate AppShell & Data-Driven Navigation**:
   - An `AppShell` component contains:
     - Header: Application brand title, employee profile summary (`EmpInfo`), and Logout action.
     - Sidebar: Collapsible navigation menu generated from an array of `NavItem` objects.
     - Main Viewport: `<router-outlet>` hosting active route views.
   - Standard routes:
     - `/` -> redirect to `/dashboard`
     - `/dashboard` -> `Dashboard` component (protected by `AuthGuard`)
     - `**` -> redirect to `/dashboard`

## Testing Decisions

- **Single High-Level Seam: HTTP Network Boundary (`HttpTestingController`)**:
  - Tests will target the entire bootstrap and authentication workflow at the highest feasible boundary using Angular's DI and `HttpTestingController` (via `provideHttpClientTesting()`).
  - Good tests verify external system behavior:
    1. Setting `authApiUrl` to `"/Apigateway/MesApiCore"` verifies that outgoing HTTP requests target `/Apigateway/MesApiCore/VerifyCert` or `/Apigateway/MesApiCore/RefreshToken`.
    2. Setting `authApiUrl` to `"http://tncimweb1.cminl.oa/uacapi/adAuth"` verifies that outgoing requests target the full absolute URL.
    3. Setting `authApiUrl` with trailing slashes (e.g. `"/Apigateway/MesApiCore/"`) verifies that no duplicate slashes (`//`) appear in requested URLs.
    4. Enabling `mockAuth: true` verifies that `currentUser()` is populated immediately without issuing any network requests.
    5. A 401 response from a business endpoint verifies that a refresh call to `${authApiUrl}/RefreshToken` is dispatched and original request replayed.
- **Prior Art**:
  - Existing specs `auth.service.spec.ts` and `jwt.interceptor.spec.ts` provide baseline mocking patterns.

## Out of Scope

- Modifying corporate backend AD Auth / UAC / API Gateway server implementations.
- MES-specific shop permission queries (to be implemented in downstream application-specific tracks).
- Dark mode theme switching.
- User profile editing or password management.

## Further Notes

- Aligns strictly with `GLOSSARY.md` domain terminology (`RuntimeConfig`, `CertificateKey`, `RefreshToken`, `EmpInfo`, `ShopContext`, `MockAuth`, `AppShell`, `NavItem`, `Scaffold`).
- Strictly respects `ADR-0001` (SSO initialization & silent refresh) and `ADR-0002` (Runtime configuration via assets/config.json).
