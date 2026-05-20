# API Endpoints

This document lists the SafeExchange REST API endpoints. All endpoints require a valid Microsoft Entra ID Bearer token.

## Secrets

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v2/secret/{secretId}` | Create a new secret (accepts optional `auditEnabled: true` in the body to opt the secret into audit logging at creation time) |
| GET | `/v2/secret/{secretId}` | Get secret metadata |
| PATCH | `/v2/secret/{secretId}` | Update secret metadata |
| DELETE | `/v2/secret/{secretId}` | Delete a secret |
| GET | `/v2/secret` | List secrets accessible to the caller |
| GET | `/v2/secret/{secretId}/audit` | Read the audit log for the live instance of a secret. Requires `Read`. Returns `{ auditEnabled: false, events: [] }` when audit was not enabled. Query params: `from`, `to` (ISO-8601 UTC), `pageSize` (max 500), `continuation`, `raw` (return un-merged `ContentRead` events), `direction` (`asc` (default) or `desc`; when `desc`, page newest-first and `continuation` walks toward older events). |

## Content

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v2/secret/{secretId}/content/{contentId}` | Create a content item |
| GET | `/v2/secret/{secretId}/content/{contentId}` | Get content metadata |
| PATCH | `/v2/secret/{secretId}/content/{contentId}` | Update content metadata |
| DELETE | `/v2/secret/{secretId}/content/{contentId}` | Delete a content item |

## Chunks (File Upload / Download)

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v2/secret/{secretId}/content/{contentId}/chunk/{chunkId}` | Upload an encrypted chunk |
| GET | `/v2/secret/{secretId}/content/{contentId}/chunk/{chunkId}` | Download a decrypted chunk |
| DELETE | `/v2/secret/{secretId}/content/{contentId}/chunk/{chunkId}` | Delete a chunk |

## Access Control

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v2/access/{secretId}` | Grant permissions to users/groups |
| GET | `/v2/access/{secretId}` | List current permissions for a secret |
| PATCH | `/v2/access/{secretId}` | Atomically add and/or remove permissions in one transaction |
| DELETE | `/v2/access/{secretId}` | Revoke permissions |

## Self-revocation (Access Give-Up)

> Available only when `Features.UseAccessGiveUp` is enabled. When the flag is off, both endpoints respond `204 No Content`.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v2/access-giveup/{secretId}` | Preview: would the caller's give-up orphan the secret, and at what scheduled expiry |
| DELETE | `/v2/access-giveup/{secretId}` | Remove the caller's direct permission row; secret is scheduled for purge if no `CanGrantAccess` holder remains |

When `Features.UseAccessGiveUp` is enabled, the existing `DELETE /v2/access/{secretId}` and `PATCH /v2/access/{secretId}` endpoints also evaluate the orphan rule after their permission changes. A secret that ends up with no remaining `CanGrantAccess` custodian is scheduled for grace-period purge (`OrphanedSecret.GracePeriod` in configuration, default 7 days).

## Access Requests

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v2/access-requests/{secretId}` | Request access to a secret |
| GET | `/v2/access-requests/{secretId}` | List access requests for a secret |
| POST | `/v2/access-requests/{secretId}/{requestId}` | Approve or deny a request |

## Webhooks

| Method | Path | Description |
|--------|------|-------------|
| POST | `/v2/webhooks/{secretId}` | Register a webhook subscription |
| GET | `/v2/webhooks/{secretId}` | List webhook subscriptions |
| DELETE | `/v2/webhooks/{secretId}/{subscriptionId}` | Remove a webhook subscription |

## Groups

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v2/groups/search` | Search Entra ID groups by name |
| GET | `/v2/groups/pinned` | List the caller's pinned groups |
| POST | `/v2/groups/pinned` | Pin a group for quick access |
| DELETE | `/v2/groups/pinned/{groupId}` | Unpin a group |

## Notifications

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v2/notifications` | Get pending notifications for the caller |
| POST | `/v2/notifications/{notificationId}` | Mark a notification as read |

## Admin

| Method | Path | Description |
|--------|------|-------------|
| POST | `/admin/applications` | Register an application identity |
| GET | `/admin/applications` | List registered applications |
| DELETE | `/admin/applications/{appId}` | Remove an application |

## Common Response Patterns

All endpoints return standard HTTP status codes:

- `200 OK` — successful operation with response body
- `201 Created` — resource created successfully
- `204 No Content` — successful operation with no response body
- `400 Bad Request` — invalid input or request body
- `401 Unauthorized` — missing or invalid authentication token
- `403 Forbidden` — authenticated but insufficient permissions
- `404 Not Found` — resource does not exist
- `409 Conflict` — resource already exists
