# Migrate Package Management and Build Tooling to pnpm

## Context

As an enterprise Angular starter template serving multiple manufacturing and intranet systems, dependency installation speed, disk storage efficiency across multi-project environments, and strict dependency isolation (preventing phantom dependencies) are critical. The original template used `npm` (`npm@11.6.2`), which relied on flat `node_modules` hoisting, leading to slower CI build times and redundant package copies across scaffolded repositories on developer workstations.

## Decision

We standardize package management and build tooling on **pnpm** (pinned to `pnpm@12.6.0`):

1. **Tooling Pin**: Declared `"packageManager": "pnpm@12.6.0"` in `package.json` and `"cli": { "packageManager": "pnpm" }` in `angular.json` to instruct Angular CLI and Node tools to invoke pnpm.
2. **Deterministic Migration**: Used `pnpm import` to convert the proven `package-lock.json` into `pnpm-lock.yaml`, guaranteeing identical dependency versions from the corporate private registry (`http://10.53.216.186:4873/`).
3. **Build Script Security Policy**: Introduced `pnpm-workspace.yaml` with explicit `allowBuilds` for `@parcel/watcher`, `esbuild`, `lmdb`, and `msgpackr-extract` to satisfy pnpm v10+/v12+ supply-chain security controls without blocking automated or CI installation.
4. **Scaffolding Automation**: Updated `scaffold.ps1` to detect and run `pnpm install`, and updated project documentation and VS Code configurations accordingly.

## Considered Options

- **npm (Status Quo)**: Rejected due to flat dependency hoisting vulnerabilities (phantom dependencies) and slow multi-project installation cycles on developer machines.
- **Yarn (v1 / Berry)**: Evaluated, but pnpm offers native symlinked hard-link store architecture, superior disk deduplication on Windows machines, and tighter Angular CLI integration.

## Consequences

- Fast and disk-efficient package installation via global hard-link content-addressable store.
- Strict dependency tree resolution prevents phantom imports.
- Scaffolded applications and CI pipelines must have `pnpm` available on the host system.
