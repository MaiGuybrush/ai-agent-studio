# Runtime Configuration via assets/config.json

## Context
In enterprise multi-site manufacturing environments, frontend applications are deployed across distinct domains and fabs with differing authentication and backend API gateways. Embedding API endpoints in Angular's compile-time `environment.ts` requires separate build artifacts for each deployment target, violating "Build Once, Deploy Anywhere" principles and overburdening CI/CD pipelines.

## Decision
We adopt runtime configuration by loading `assets/config.json` via HTTP during `APP_INITIALIZER` prior to executing authentication or application workflows:
1. **File Location**: Stored at `src/assets/config.json` and published to `/assets/config.json`.
2. **Bootstrapping Sequence**: A dedicated `ConfigService` retrieves configuration before `AuthService` attempts SSO verification or session restoration.
3. **Multi-Domain & Mocking**: `config.json` specifies `authApiUrl` (supporting relative paths or explicit domains) and a `mockAuth` toggle for offline local development without intranet VPN.

## Considered Options
- **Compile-time `environment.ts`**: Rejected because changing host endpoints across sites requires rebuilding the entire Angular bundle per target domain.
- **Root-level `public/config.json`**: Evaluated, but standardized on `src/assets/config.json` to maintain compatibility with internal enterprise deployment scripts and asset bundling conventions.

## Consequences
- A single compiled distribution artifact can be deployed across all enterprise domains simply by overriding `assets/config.json`.
- The application incurs a single lightweight static GET request (`/assets/config.json`) on initial load before UI rendering.
