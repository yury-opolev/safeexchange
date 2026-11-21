# Architecture Overview

This document describes the high-level architecture of SafeExchange backend.

## System Diagram

```
                          ┌──────────────────┐
                          │   Web / Mobile   │
                          │     Clients      │
                          └────────┬─────────┘
                                   │ HTTPS + Bearer Token
                                   ▼
                          ┌──────────────────┐
                          │  Azure Functions  │
                          │   (API Layer)     │
                          │   .NET 8 / v4     │
                          └──┬───┬───┬───┬───┘
                             │   │   │   │
              ┌──────────────┘   │   │   └──────────────┐
              ▼                  ▼   ▼                  ▼
     ┌────────────────┐  ┌───────────────┐  ┌────────────────┐
     │  Azure Cosmos  │  │ Azure Blob    │  │ Azure KeyVault │
     │  DB (NoSQL)    │  │ Storage       │  │ (Encryption)   │
     │                │  │ (Files/Chunks)│  │                │
     └────────────────┘  └───────────────┘  └────────────────┘
              │                                     │
              │          ┌───────────────┐          │
              └─────────►│ Azure Queue   │◄─────────┘
                         │ Storage       │
                         │ (Async Tasks) │
                         └───────┬───────┘
                                 │
                                 ▼
                         ┌───────────────┐
                         │   Webhooks    │
                         │  (External)   │
                         └───────────────┘
```

## Components

### API Layer — Azure Functions

The API is a set of Azure Functions (v4, isolated worker model) running on .NET 8. Each function is an HTTP-triggered endpoint that handles a specific operation (create secret, grant access, download chunk, etc.).

Functions are thin wrappers — they authenticate the caller, then delegate to business logic handlers in `SafeExchange.Core.Functions`, which build on the host-agnostic domain logic in `SafeExchange.Core`.

Key endpoint groups:

| Endpoint Pattern | Purpose |
|-----------------|---------|
| `POST/GET/PATCH/DELETE /{v}/secret/{secretId}` | Create, read, update, delete secrets (`{v}` = `v2` or `v3`) |
| `GET /{v}/secret-list` | List the caller's secrets |
| `POST /{v}/secret/{secretId}/content`, `PATCH/DELETE .../content/{contentId}` | Create and manage content items within a secret |
| `POST .../content/{contentId}/chunk`, `PATCH .../commit`, `GET .../chunk/{chunkId}`, `GET .../all` | Upload, commit and download encrypted file chunks |
| `POST/GET/PATCH/DELETE /{v}/access/{secretId}` | Grant, list, change or revoke permissions |
| `POST/PATCH/DELETE /{v}/accessrequest/{secretId}`, `GET /{v}/accessrequest-list` | Request and approve/deny access |
| `/{v}/pinnedgroups*`, `/{v}/group-search`, `/{v}/groups-list` | Search and pin Entra ID groups |
| `POST /api/mcp`, `GET /api/.well-known/oauth-protected-resource` | First-party MCP endpoint (feature-flagged, see below) |

The full list is in [api-endpoints.md](api-endpoints.md).

### MCP Endpoint

AI agents reach SafeExchange through a first-party [MCP](mcp.md) endpoint (`POST /api/mcp`, stateless streamable HTTP, sixteen tools) behind `Features.McpEndpoint`. Layering:

- `SafeExchange.Functions` holds the two triggers (`SafeMcp`, `SafeOAuthMetadata`).
- `SafeExchange.Core.Functions/Mcp` adapts them (`McpHandler`: feature flag, caller resolution, body, session header; `OAuthProtectedResourceHandler`).
- `SafeExchange.Core/Mcp` holds the host-agnostic JSON-RPC dispatcher and the tools. The tools call the same `SafeExchange.Core/Services` as the REST handlers, with a `Caller` whose channel is `mcp`, so permissions, validation and audit are identical to REST; audit events additionally record the channel and the client's self-reported label.

The same bearer-token middleware protects `/api/mcp`; with MCP enabled, `401` responses carry a `WWW-Authenticate` challenge pointing to the protected-resource metadata. Projects that host their own Functions app and authentication: see [consumer-migration.md](consumer-migration.md).

### Data Layer — Cosmos DB

All metadata is stored in Azure Cosmos DB using Entity Framework Core (Cosmos provider). The database contains the following entity types:

- **ObjectMetadata** — top-level secret: name, creator, status, expiration policy.
- **ContentMetadata** — individual content items (files) within a secret, supporting multiple versions.
- **ChunkMetadata** — manifest entries tracking encrypted blob chunks for each content item.
- **SubjectPermissions** — permission grants linking a subject (user/group/app) to a secret with specific permission flags.
- **AccessRequest** — pending or resolved access requests with approval workflow state.
- **User / Application** — cached identity records from Entra ID.
- **ExpirationMetadata** — expiration schedule or idle-timeout configuration per secret.
- **WebhookSubscription** — registered webhook endpoints for event notifications.

Each entity type is stored in its own Cosmos DB container with a partition key chosen for query efficiency and horizontal scaling.

### File Storage — Azure Blob Storage

Secret content (files, text payloads) is stored as encrypted blobs in Azure Storage. Large files are split into chunks, each encrypted independently via Azure KeyVault before being written to blob storage. This design supports:

- Streaming uploads and downloads of arbitrarily large files.
- Per-chunk encryption so no single blob contains the full plaintext.
- Efficient partial reads for large payloads.

### Encryption — Azure KeyVault

All sensitive data at rest is encrypted using keys managed by Azure KeyVault. The service never handles raw encryption keys in memory for longer than necessary — it delegates key operations to KeyVault's HSM-backed APIs. Configuration secrets (connection strings, API keys) are also stored in KeyVault and referenced by the Functions app at startup.

### Async Processing — Azure Queue Storage

Time-delayed and background tasks (webhook delivery, notification retries) are dispatched via Azure Queue Storage. A queue-triggered function picks up messages and processes them asynchronously, decoupling webhook delivery from the main request flow.

## Project Structure

```
SafeExchange.Core/
├── Audit/                 — Hash-chained secret audit events (IAuditWriter, AuditOrigin: channel + client label)
├── Blob/                  — Blob storage helpers (upload, download, encrypt)
├── Callers/               — Caller, ICallerResolver/CallerResolver, GlobalAccessPolicy (host-agnostic caller resolution)
├── Configuration/         — Settings classes (CosmosDb, Auth, Features)
├── Crypto/                — Encryption/decryption using KeyVault
├── DatabaseContext/        — EF Core DbContext for Cosmos DB
├── DelayedTasks/          — Queue-based async task scheduling
├── Functions/             — Timer/queue handlers (purge, webhook notifications)
├── Graph/                 — Microsoft Graph API client (user/group lookup)
├── Mcp/                   — Host-agnostic MCP JSON-RPC dispatcher and the 16 tools (depend only on Services)
├── Model/                — Domain entity classes
├── Permissions/           — Permission evaluation engine
├── Purger/               — Expiration and data cleanup logic
├── Requests/             — Request/response DTOs
└── Services/             — Host-agnostic services (SecretService, ContentService, ChunkService, AccessService, AuditQueryService) taking a Caller and returning ServiceResult

SafeExchange.Core.Functions/  — Azure Functions-typed code (references SafeExchange.Core, never the reverse)
├── Functions/             — HTTP handlers for each endpoint (the secret, content, chunk, commit, access and audit handlers are thin adapters over Core's Services)
├── Middleware/            — Authentication middleware (JWT validation), token filter, session and API-version telemetry
├── Mcp/                   — McpHandler (/api/mcp), OAuthProtectedResourceHandler, McpRoutes
├── Requests/Filters/      — Global authorization filters (HTTP wrappers; the global-group decision is Core's GlobalAccessPolicy)
├── Utilities/             — ActionResults, TokenHelperHttpExtensions, CallerDenialHttpExtensions, ServiceResultHttpExtensions
└── SafeExchangeFunctionsStartup.cs — Worker middleware and Functions-typed DI registrations

SafeExchange.Functions/
├── Functions/             — HTTP-triggered Azure Function definitions, including the MCP triggers (SafeMcp, SafeOAuthMetadata); handlers come from DI
├── AdminFunctions/        — Admin-only endpoints
└── Program.cs            — DI container setup and app bootstrap

SafeExchange.Tests/
├── Architecture/          — Guards (for example, Core references no Azure Functions types)
├── Contract/              — REST wire-contract tests of the secret, content, chunk, access and audit handlers
├── Mcp/                   — MCP dispatcher, tools and endpoint tests
├── Services/              — Service and caller-resolution tests
└── Tests/                — Unit and integration tests (NUnit + Moq)

deployment/
└── current/arm/          — ARM templates for Azure resource deployment
```
