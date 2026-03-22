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

Functions are thin wrappers — they authenticate the caller, then delegate to business logic handlers in `SafeExchange.Core`.

Key endpoint groups:

| Endpoint Pattern | Purpose |
|-----------------|---------|
| `POST/GET/PATCH/DELETE /v2/secret/{secretId}` | Create, read, update, delete secrets |
| `POST/GET /v2/secret/{secretId}/content/{contentId}` | Manage content items within a secret |
| `POST/GET /v2/secret/{secretId}/content/{contentId}/chunk/{chunkId}` | Upload/download encrypted file chunks |
| `POST/GET/DELETE /v2/access/{secretId}` | Grant, list, or revoke permissions |
| `POST/GET /v2/access-requests/{secretId}` | Request and approve/deny access |
| `GET /v2/webhooks/{secretId}` | Manage webhook subscriptions |
| `GET /v2/groups/*` | Search and pin Entra ID groups |

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
├── Blob/                  — Blob storage helpers (upload, download, encrypt)
├── Configuration/         — Settings classes (CosmosDb, Auth, Features)
├── Crypto/                — Encryption/decryption using KeyVault
├── DatabaseContext/        — EF Core DbContext for Cosmos DB
├── DelayedTasks/          — Queue-based async task scheduling
├── Filters/               — Global authorization filters
├── Functions/             — Business logic handlers for each endpoint
├── Graph/                 — Microsoft Graph API client (user/group lookup)
├── Middleware/            — Authentication middleware (JWT validation)
├── Model/                — Domain entity classes
├── Permissions/           — Permission evaluation engine
├── Purging/              — Expiration and data cleanup logic
└── Requests/             — Request/response DTOs

SafeExchange.Functions/
├── Functions/             — HTTP-triggered Azure Function definitions
├── AdminFunctions/        — Admin-only endpoints
└── Program.cs            — DI container setup and app bootstrap

SafeExchange.Tests/
└── Tests/                — Unit and integration tests (NUnit + Moq)

deployment/
└── current/arm/          — ARM templates for Azure resource deployment
```
