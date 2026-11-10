# AiAgentStudioWeb

A standardized enterprise Angular application template providing corporate SSO authentication, application shell scaffolding, and unified frontend conventions.

## Authentication & Identity

**CertificateKey**:
A single-use credential key supplied in the query string by the corporate identity provider upon redirection back to the application.
_Avoid_: Token, Ticket, AuthCode

**RefreshToken**:
A long-lived corporate authentication credential used to obtain a new JSON Web Token without re-prompting the user.
_Avoid_: SessionKey, Secret

**EmpInfo**:
The authenticated corporate employee profile returned by the authentication service, containing user identity and authorization metadata.
_Avoid_: UserData, AccountProfile, CurrentUser

**ShopContext**:
The specific manufacturing fab or departmental scope within which the employee's operations and authorizations apply.
_Avoid_: Tenant, Workspace, Department

**MockAuth**:
A development-mode authentication bypass that supplies synthetic employee identity without contacting corporate auth servers.
_Avoid_: FakeUser, DevBypass, TestLogin

## Architecture & Configuration

**RuntimeConfig**:
The externalized application configuration loaded at runtime from `assets/config.json` before bootstrapping.
_Avoid_: EnvironmentConfig, StaticSettings, AppEnv

**AppShell**:
The persistent enterprise layout wrapper containing the corporate header, collapsible sidebar, and route viewport.
_Avoid_: MasterLayout, Frame, MainContainer

**NavItem**:
A structured navigation menu descriptor defining route target, display label, icon, and optional authorization constraints.
_Avoid_: MenuItem, LinkConfig, SidebarEntry

## Template & Tooling

**Scaffold**:
The automated bootstrapping mechanism that provisions a new standalone application repository from this starter template.
_Avoid_: Generator, Cloner, Quickstart
