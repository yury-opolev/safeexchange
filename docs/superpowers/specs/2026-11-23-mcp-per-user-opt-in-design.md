# Per-user MCP opt-in — design

Date: 2026-11-23
Status: approved in brainstorming (2026-11-23)
Repositories: `safeexchange` (backend), `safeexchange.blazorpwa` (PWA + AdminPanel)

## Goal

A user must opt in before their own identity can be used through the MCP endpoint (`/api/v1/mcp`). The default is **off** for new and existing users. An administrator can **block** MCP for a user; a block overrides the user's choice and the user sees that the block came from an administrator, both in the web app and through the API.

## Scope

- Applies to **user** callers only. S2S applications are unaffected; their existing `enabled` switch remains the kill switch for them.
- REST and the web app are unaffected.
- Admins can only block and unblock. Turning MCP on is always the user's own choice.
- No MCP tool reads or changes these settings: an agent must not be able to grant itself MCP access through MCP.

### Known limitation (documented, accepted)

The setting controls use of SafeExchange **through MCP**. Tokens obtained through the MCP sign-in broker are ordinary Entra tokens for the SafeExchange API (in production the broker uses the same app as the web app), so software holding a user's token can still call REST, including `PATCH /me/settings`. A dedicated broker app registration plus REST enforcement would close this and is a possible later upgrade; it is out of scope here.

## Data

Two new properties on `SafeExchange.Core.Model.User`:

| Property | Set by | Default | Meaning |
|---|---|---|---|
| `McpAllowed` (bool) | the user | `false` | The user allows MCP for their account (opt-in). |
| `McpBlockedByAdmin` (bool) | administrators | `false` | Admin block; overrides `McpAllowed`. |

Effective permission: `McpAllowed && !McpBlockedByAdmin`.

Existing user documents get both properties as `false` through the existing backfill mechanism (as `TelemetryIdBackfill` / `MigrationItem00012_User` did for earlier fields). New users are created with both `false`.

## Enforcement (MCP endpoint)

In `McpHandler`, after caller resolution (which already runs on every MCP message), for a **user** caller:

| State | Result |
|---|---|
| allowed, not blocked | proceed as today |
| not allowed, not blocked | HTTP `403`, `status = "forbidden"`, `subStatus = "mcp_not_allowed"`, `error = "MCP access is not enabled for this account. Turn on 'Allow MCP' in SafeExchange settings."` |
| blocked (regardless of `McpAllowed`) | HTTP `403`, `status = "forbidden"`, `subStatus = "mcp_blocked_by_admin"`, `error = "MCP access is blocked for this account by an administrator."` |

- The body uses the existing `BaseResponseObject` shape (`status`, `subStatus`, `error`), as the existing disabled-user/disabled-app denials do.
- Changes take effect on the next MCP message (no caching beyond the per-message resolution).
- Denials are logged at Information with telemetry identifiers only (no UPN or other personal data), like existing denials.
- The OAuth broker is unchanged: it does not inspect tokens, so sign-in still completes; the first MCP message is refused with the reason.

## API

### User: own settings (REST only)

Users only. An S2S application calling these endpoints gets `403` (`status = "forbidden"`, error "Settings are available to users only.").

`GET /api/{apiVersion}/me/settings`

```json
{ "status": "ok", "result": { "mcpAllowed": false, "mcpBlockedByAdmin": false, "mcpEffective": false } }
```

`PATCH /api/{apiVersion}/me/settings`, body `{ "mcpAllowed": true }`

- `mcpAllowed` is required and must be a JSON boolean; otherwise `400` (`status = "error"`).
- Enabling while blocked: `403`, `status = "forbidden"`, `subStatus = "mcp_blocked_by_admin"`, error "MCP is blocked for this account by an administrator." Nothing is stored.
- Disabling is always accepted (including while blocked).
- Success returns the same body as `GET`.
- The resource is named `settings` so other personal settings can be added later; only `mcpAllowed` is writable now.

### Admin

- `GET /api/{apiVersion}/admin/users` and `GET /api/{apiVersion}/admin/users/{upn}` additionally return `mcpAllowed` and `mcpBlockedByAdmin`.
- `PATCH /api/{apiVersion}/admin/users/{upn}/mcp-blocked`, body `{ "blocked": bool }`: same shape and behaviour as the existing `PATCH .../admin/users/{upn}/enabled` (admin filter, `404` unknown user, `400` bad body, returns the updated user).

### Concurrency

Both write paths (`PATCH /me/settings`, `PATCH /admin/users/{upn}/mcp-blocked`) use the existing etag concurrency pattern of the `enabled` toggle: the user document can be updated concurrently (telemetry-id rotation), so on a conflict reload the latest document, re-apply the change and save again (bounded attempts). The `/me/settings` enable path re-checks `McpBlockedByAdmin` on every attempt so a block that lands concurrently wins.

## Frontend (`safeexchange.blazorpwa`)

- **PWA `SettingsDialog`** (`SafeExchange.Client.Web.Components/Shared/SettingsDialog.razor`): an "Allow MCP (AI agents)" switch with a one-line explanation (lets AI tools such as Copilot CLI act on your secrets through SafeExchange's MCP endpoint). Loads from `GET /me/settings`, saves with `PATCH /me/settings`. When `mcpBlockedByAdmin` is true the switch is off and disabled with the notice "Blocked by an administrator. Contact your SafeExchange administrator." Errors are shown inline; the switch reverts on failure.
- **AdminPanel `Users.razor`**: an MCP column with `Off` / `On` / `Blocked` (Blocked wins).
- **AdminPanel `ManageUser.razor`**: shows the user's own choice (read-only) and a "Block MCP" toggle calling `PATCH .../mcp-blocked`.
- Client API methods and models in the shared client library, following the existing client patterns.

## Delivery

1. Backend PR: flags + backfill, MCP enforcement, `/me/settings`, admin list/detail fields, `mcp-blocked` endpoint, route guard test update, docs (`docs/mcp.md`, `docs/api-endpoints.md`).
2. Frontend PR: settings switch, AdminPanel column and toggle, client API/models, component tests.

Rollout: backend to staging, verify, then production (from then on MCP is refused for every user until they opt in). Until the frontend ships, a user can opt in with `PATCH /me/settings`. Then the frontend to staging, verify, then production.

## Testing

Backend (TDD):
- MCP enforcement: user not allowed → `403 mcp_not_allowed`; allowed → succeeds; blocked (with and without `McpAllowed`) → `403 mcp_blocked_by_admin`; S2S caller unaffected; REST unaffected.
- `/me/settings`: GET defaults; PATCH enable/disable; S2S caller → `403`; bad/missing/non-boolean body → `400`; enable while blocked → `403` and nothing stored; disable while blocked → `200`.
- Admin: list/detail include both fields; `mcp-blocked` → `404` unknown user, `400` bad body, block/unblock persists, concurrency retry re-applies.
- Backfill sets both fields `false` on existing users and is idempotent.
- Route guard test includes the new routes.

Frontend:
- Settings switch renders state, saves, reverts on failure; blocked state disables it and shows the notice.
- AdminPanel column states; block toggle calls the endpoint and updates the view.
