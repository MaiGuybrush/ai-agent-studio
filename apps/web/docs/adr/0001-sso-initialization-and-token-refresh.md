# SSO Initialization and Token Refresh Architecture

## Context
Enterprise applications require seamless Single Sign-On (SSO) integration with the corporate Active Directory / UAC service. Applications must bootstrap authentication state prior to rendering UI components, securely exchange URL `CertificateKey` credentials, and transparently handle token expiry during HTTP calls without interrupting user workflows.

## Decision
We adopt Angular's `APP_INITIALIZER` with Angular Signals for state storage, combined with a centralized HTTP interceptor (`JwtInterceptor`) for silent token refreshing:
1. **Bootstrapping via `APP_INITIALIZER`**: The application resolves SSO state before completing initialization. If a `CertificateKey` query parameter is present, it is verified immediately and removed from the browser URL; otherwise, session restoration is attempted via stored `RefreshToken`.
2. **Signal-based Auth State**: Authentication state (`EmpInfo`, token) is managed via Angular Signals within `AuthService`, replacing heavier state management libraries (e.g., NgRx).
3. **Transparent 401 Interception**: Outgoing HTTP requests are enriched with `Bearer {token}`. On HTTP 401 responses, the interceptor queues incoming requests, triggers a single `RefreshToken` request to `adAuth`, updates credentials, and replays failed requests.

## Considered Options
- **NgRx Store & Effects**: Rejected because it introduces excessive boilerplate and indirection for a template intended to serve diverse internal applications with minimal architectural overhead.
- **Route Guard Redirect Only**: Rejected because initializing authentication at the route level produces UI flashing and race conditions when fetching initial configuration data before route resolution.

## Consequences
- All HTTP calls needing authentication rely on the single `JwtInterceptor`.
- If silent refresh fails with 401, all queued requests fail and the user is redirected to the corporate SSO login portal.
- Sub-applications created from this template do not need additional SSO handling boilerplate.
