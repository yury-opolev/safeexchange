# API Endpoints

This document lists the SafeExchange REST API endpoints, as defined by the HTTP triggers in `SafeExchange.Functions`. All endpoints require a valid Microsoft Entra ID Bearer token, except the MCP discovery and OAuth broker routes (`/.well-known/*` at the host root, `/api/oauth/*`; see [MCP](#mcp)).

Routes are served under the Azure Functions `/api` prefix, so `/{v}/secret/{secretId}` is `https://<host>/api/v3/secret/{secretId}`.

## Versions

`{v}` is the API version route segment. The web app and older deployed builds use `v2` and `v3`; both are kept working unchanged. The two versions share every route and differ only in these response shapes:

| Endpoint | `v2` | `v3` |
|----------|------|------|
| `GET /{v}/secret-list` | Secrets with direct permissions | Adds the caller's effective (direct + group) permissions |
| `GET /{v}/access/{secretId}` | Plain access list | Enriched access list |
| `GET /{v}/pinnedsecrets-list` | Direct permissions | Effective permissions |

A few routes are fixed at `v1` (webhooks, see below). The version used by each request is recorded in telemetry as `saex.apiVersion`.

## Secrets

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/secret/{secretId}` | Create a new secret (accepts optional `auditEnabled: true` in the body to opt the secret into audit logging at creation time) |
| GET | `/{v}/secret/{secretId}` | Get secret metadata, including its content items |
| PATCH | `/{v}/secret/{secretId}` | Update secret metadata |
| DELETE | `/{v}/secret/{secretId}` | Delete a secret |
| GET | `/{v}/secret-list` | List secrets accessible to the caller (shape differs between `v2` and `v3`) |
| GET | `/{v}/secret/{secretId}/audit` | Read the audit log for the live instance of a secret. Requires `Read`. Returns `{ auditEnabled: false, events: [] }` when audit was not enabled. Query params: `from`, `to` (ISO-8601 UTC), `pageSize` (max 500), `continuation`, `raw` (return un-merged `ContentRead` events), `direction` (`asc` (default) or `desc`; when `desc`, page newest-first and `continuation` walks toward older events). Events produced through MCP carry optional `channel` and `clientLabel` fields (omitted otherwise). |

## Content

A secret holds content items: the main text value plus attached files. Content metadata is returned as part of `GET /{v}/secret/{secretId}`.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/secret/{secretId}/content` | Create a content item. The server assigns the content id (returned as `result.contentName`). Body: `{ "contentType", "fileName", "isImage" }` |
| PATCH | `/{v}/secret/{secretId}/content/{contentId}` | Update content metadata (content type, file name) |
| DELETE | `/{v}/secret/{secretId}/content/{contentId}` | Delete a content item |
| PATCH | `/{v}/secret/{secretId}/content/{contentId}/drop` | Delete all uploaded data of a content item and keep the blank item (for example to abandon an interrupted upload) |

## Chunks (File Upload / Download)

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/secret/{secretId}/content/{contentId}/chunk` | Upload the next chunk (request body = raw bytes; the server assigns the chunk id). Requires `Write` |
| PATCH | `/{v}/secret/{secretId}/content/{contentId}/commit` | Finish a hashed upload. Body: `{ "hash": "<SHA-256 hex of the whole content>" }`. Requires the upload ticket header |
| GET | `/{v}/secret/{secretId}/content/{contentId}/chunk/{chunkId}` | Download one decrypted chunk. Requires `Read` |
| GET | `/{v}/secret/{secretId}/content/{contentId}/all` | Download the whole content as one stream. Requires `Read` |

Upload headers (`POST .../chunk` and `PATCH .../commit`):

| Header | Purpose |
|--------|---------|
| `X-SafeExchange-Ticket` | Upload ticket. Omit on the first chunk; the response carries `result.accessTicket`, which must be sent with every later chunk and with the commit. While a ticket is held, other writers get `422 Content is being updated.` |
| `X-SafeExchange-Chunk-Hash` | SHA-256 (hex) of this chunk. Present ⇒ hashed upload, which must be finished with `commit` and is verified end to end. Absent ⇒ legacy upload (only while `Features.AllowLegacyAttachmentUploads` is on; `Features.IgnoreChunkHashHeader` ignores the header, and the main text content always ignores it). Hashed and legacy chunks cannot be mixed in one content item |
| `X-SafeExchange-OpType` | Legacy uploads only: `interim` keeps the upload open; any other value finishes it |

The upload ticket is an upload-session lock used together with the bearer token; it is not a credential. Chunks are uploaded in order; the typical flow is `POST .../content` → `POST .../chunk` (repeat) → `PATCH .../commit`, and `GET .../all` (or `GET .../chunk/{chunkId}` per chunk) to read back. Chunk ids come from the content's chunk list in `GET /{v}/secret/{secretId}`.

## Access Control

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/access/{secretId}` | Grant permissions to users/groups |
| GET | `/{v}/access/{secretId}` | List current permissions for a secret (plain list on `v2`, enriched on `v3`) |
| PATCH | `/{v}/access/{secretId}` | Atomically add and/or remove permissions in one transaction |
| DELETE | `/{v}/access/{secretId}` | Revoke permissions |

## Self-revocation (Access Give-Up)

> Available only when `Features.UseAccessGiveUp` is enabled. When the flag is off, both endpoints respond `204 No Content`.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/{v}/access-giveup/{secretId}` | Preview: would the caller's give-up orphan the secret, and at what scheduled expiry |
| DELETE | `/{v}/access-giveup/{secretId}` | Remove the caller's direct permission row; secret is scheduled for purge if no `CanGrantAccess` holder remains |

When `Features.UseAccessGiveUp` is enabled, the existing `DELETE /{v}/access/{secretId}` and `PATCH /{v}/access/{secretId}` endpoints also evaluate the orphan rule after their permission changes. A secret that ends up with no remaining `CanGrantAccess` custodian is scheduled for grace-period purge (`OrphanedSecret.GracePeriod` in configuration, default 7 days).

## Access Requests

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/accessrequest/{secretId}` | Request access to a secret |
| PATCH | `/{v}/accessrequest/{secretId}` | Approve or deny a request (the request id is in the body) |
| DELETE | `/{v}/accessrequest/{secretId}` | Delete an access request (the request id is in the body) |
| GET | `/{v}/accessrequest-list` | List the caller's pending outgoing requests and the incoming requests awaiting the caller |

## Pinned Secrets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/{v}/pinnedsecrets-list` | List the caller's pinned secrets, newest-pin first |
| GET | `/{v}/pinnedsecrets/{secretId}` | Check whether the caller has pinned `{secretId}` |
| PUT | `/{v}/pinnedsecrets/{secretId}` | Pin a secret. Requires `Read`. Capped at `PinnedSecrets.MaxPinnedSecretsPerUser` (default 5) |
| DELETE | `/{v}/pinnedsecrets/{secretId}` | Unpin a secret (idempotent) |

The list / single-GET responses always return a DTO with `exists`, `canRead`, `canWrite`, `canGrantAccess`, `canRevokeAccess`, and `tags`. When the secret has been deleted, `exists` is `false`; when the caller has lost access, all permission flags are `false` and `tags` is empty. The UI uses this to render deleted / no-access status badges.

## Pinned Groups

| Method | Path | Description |
|--------|------|-------------|
| GET | `/{v}/pinnedgroups-list` | List the caller's pinned groups |
| GET | `/{v}/pinnedgroups/{pinnedGroupId}` | Read one pinned group |
| PUT | `/{v}/pinnedgroups/{pinnedGroupId}` | Pin a group for quick access |
| DELETE | `/{v}/pinnedgroups/{pinnedGroupId}` | Unpin a group |

## Search and Lookup

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/user-search` | Search users (Microsoft Graph, when `Features.UseGraphUserSearch` is on) |
| POST | `/{v}/group-search` | Search Entra ID groups (when `Features.UseGraphGroupSearch` is on) |
| POST | `/{v}/application-search` | Search registered applications |
| GET | `/{v}/groups-list` | List groups |
| GET | `/{v}/applications-list` | List registered applications |
| GET | `/{v}/telemetry/config` | Client telemetry configuration |

## Personal Settings

Users only: an application (S2S) caller gets `403` (`Settings are available to users only.`).

| Method | Path | Description |
|--------|------|-------------|
| GET | `/{v}/me/settings` | The caller's settings: `{ "mcpAllowed": bool, "mcpBlockedByAdmin": bool, "mcpEffective": bool }` (`mcpEffective` = allowed and not blocked) |
| PATCH | `/{v}/me/settings` | Change the caller's settings. Body `{ "mcpAllowed": bool }`; a missing or non-boolean `mcpAllowed` answers `400`. Turning MCP on while an administrator blocked it answers `403` with `subStatus = "mcp_blocked_by_admin"` and stores nothing; turning it off is always accepted. Returns the same body as `GET` |

## Self-service S2S Applications

> Available only when `Features.S2SAppsSelfService` is enabled. When the flag is off, the endpoints respond `204 No Content`. These endpoints use the regular authentication gate (not admin).

| Method | Path | Description |
|--------|------|-------------|
| POST | `/{v}/s2sapps` | Register an application identity |
| GET | `/{v}/s2sapps-allowed-tenants` | Tenants from which app-only tokens are accepted |
| GET | `/{v}/me/s2sapps` | List the applications the caller owns |
| GET | `/{v}/s2sapps/{displayName}` | Application detail |
| DELETE | `/{v}/s2sapps/{displayName}` | Remove an application |
| PATCH | `/{v}/s2sapps/{displayName}/enabled` | Enable or disable an application |
| GET | `/{v}/s2sapps/{displayName}/owners` | List owners |
| POST | `/{v}/s2sapps/{displayName}/owners` | Add an owner |
| PUT | `/{v}/s2sapps/{displayName}/owners` | Replace the owners |
| DELETE | `/{v}/s2sapps/{displayName}/owners/{subjectType}/{subjectId}` | Remove an owner |

## Notifications

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v1/notificationdetails/{webhookNotificationDataId}` | Details of an external (webhook) notification |

## Admin

Admin endpoints require the caller to be a configured admin (`AdminConfiguration.AdminUsers` or a member of `AdminConfiguration.AdminGroups`).

| Method | Path | Description |
|--------|------|-------------|
| GET | `/{v}/admin/applications` | List registered applications |
| GET | `/{v}/admin/applications/{displayName}` | Application detail |
| DELETE | `/{v}/admin/applications/{displayName}` | Remove an application |
| PATCH | `/{v}/admin/applications/{displayName}/enabled` | Enable or disable an application |
| PUT | `/{v}/admin/applications/{displayName}/owners` | Replace application owners |
| POST, GET, PATCH, DELETE | `/{v}/applications/{applicationId}` | Register and manage an application by id |
| GET | `/{v}/admin/users` | List users (each with `mcpAllowed` and `mcpBlockedByAdmin`) |
| GET | `/{v}/admin/users/{upn}` | User detail (including `mcpAllowed` and `mcpBlockedByAdmin`) |
| GET | `/{v}/admin/users/by-telemetry-id/{telemetryId}` | Look up a user by telemetry id |
| PATCH | `/{v}/admin/users/{upn}/enabled` | Enable or disable a user |
| PATCH | `/{v}/admin/users/{upn}/mcp-blocked` | Block or unblock MCP for a user. Body `{ "blocked": bool }` (otherwise `400`); `404` for an unknown user; returns the updated user. A block overrides the user's own `mcpAllowed` |
| GET | `/{v}/admin/secret-list` | List all secrets |
| GET | `/{v}/admin/secret/{secretName}` | Secret detail |
| GET | `/{v}/admin/secret/{secretName}/access` | Secret access list |
| GET | `/{v}/admin/audit` | Search audit events |
| GET | `/{v}/admin/audit/{auditInstanceId}` | Audit events of one secret instance |
| POST | `/{v}/admops/{operationName}` | Run an admin operation (`add_kek_version`, `run_dbmigration` with body `{ "migrationId": "<id>" }`) |
| POST | `/v1/webhooksubscriptions` | Register a webhook subscription |
| GET, PATCH, DELETE | `/v1/webhooksubscriptions/{webhookSubscriptionId}` | Read, update or remove a webhook subscription |
| GET | `/v1/webhooksubscriptions-list` | List webhook subscriptions |

## MCP

SafeExchange exposes a first-party [Model Context Protocol](https://modelcontextprotocol.io) endpoint so AI agents can use secrets and files under the same permissions and audit as people. It is off unless `Features.McpEndpoint` is enabled; when off, the anonymous metadata and OAuth broker routes respond `404`, and `/api/v1/mcp` responds `404` to authenticated callers (an unauthenticated call gets the normal `401`, without the `WWW-Authenticate` challenge, because authentication runs first). The OAuth broker routes additionally need `Mcp:OAuth:ClientId` and `Mcp:OAuth:StateKey`, are throttled per client address (`429` with `Retry-After`) and answer `404` over plain http except to localhost. See [mcp.md](mcp.md) for setup, authentication, the OAuth broker, the tool list and the file transfer flows. A user can use MCP only after opting in (`PATCH /{v}/me/settings` with `{ "mcpAllowed": true }`, see [Personal Settings](#personal-settings)); otherwise `/api/v1/mcp` answers `403` with `subStatus = "mcp_not_allowed"`, or `"mcp_blocked_by_admin"` when an administrator blocked MCP for the user. Applications are not affected.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/v1/mcp` | MCP over stateless streamable HTTP: one JSON-RPC message in, one JSON response out. Requires a Bearer token. `GET` and `DELETE` respond `405` |
| GET | `/.well-known/oauth-protected-resource/api/v1/mcp` | Anonymous RFC 9728 protected-resource metadata for MCP clients (host root; `resource` = `https://<host>/api/v1/mcp`) |
| GET | `/.well-known/openid-configuration/api` | Anonymous authorization-server metadata of the OAuth broker (host root; issuer `https://<host>/api`) |
| GET | `/.well-known/oauth-authorization-server/api` | Same document as `openid-configuration` (host root) |
| POST | `/api/oauth/register` | Anonymous RFC 7591 client registration (JSON). Loopback `redirect_uris` only; answers `201` with the configured Entra public `client_id` |
| GET | `/api/oauth/authorize` | Anonymous. Validates the authorization request (S256 PKCE, loopback `redirect_uri`, known `client_id`) and answers the consent page (`200` HTML) with the `__Host-saex-mcp-consent` cookie |
| POST | `/api/oauth/authorize` | Anonymous (form from the consent page). Continue redirects (`302`) to Entra, Cancel redirects `access_denied` to the client; a missing cookie or tampered form answers a `400` page |
| GET | `/api/oauth/callback` | Anonymous. Entra's redirect target; verifies the signed `state` and the consent cookie, then redirects (`302`) the code or error to the client's loopback `redirect_uri` |
| POST | `/api/oauth/token` | Anonymous (form). Forwards `authorization_code` / `refresh_token` grants to Entra and relays Entra's status and JSON body (`Cache-Control: no-store`) |

When MCP is enabled, `401` responses additionally carry `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource/api/v1/mcp"` (the body is unchanged). There are no `/api/.well-known/*` routes; every other root `/.well-known/*` path answers `404`.

## Common Response Patterns

All endpoints return standard HTTP status codes:

- `200 OK` — successful operation with response body
- `201 Created` — resource created successfully
- `204 No Content` — successful operation with no response body (also returned by feature-gated endpoints whose flag is off)
- `400 Bad Request` — invalid input or request body
- `401 Unauthorized` — missing or invalid authentication token
- `403 Forbidden` — authenticated but insufficient permissions
- `404 Not Found` — resource does not exist
- `409 Conflict` — resource already exists
- `422 Unprocessable Entity` — request understood but rejected (for example `Content is being updated.`)
