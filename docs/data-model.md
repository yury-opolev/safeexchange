# Data Model

This document describes the core domain entities in SafeExchange and how they relate to each other.

## Entity Relationships

```
ObjectMetadata (Secret)
 ├── ContentMetadata (file or text payload)
 │    └── ChunkMetadata (encrypted blob reference)
 ├── SubjectPermissions (who can access this secret)
 ├── AccessRequest (pending access requests)
 ├── ExpirationMetadata (when to auto-delete)
 └── WebhookSubscription (notification endpoints)
```

## Core Entities

### ObjectMetadata

The top-level entity representing a secret. Each secret has a unique name chosen by the creator.

| Field | Description |
|-------|-------------|
| ObjectName | Unique identifier / human-readable name |
| Status | Current state (e.g., active, deleted) |
| CreatedBy | UPN of the creator |
| CreatedAt | Creation timestamp |
| ExpirationMetadata | Embedded expiration policy |

### ContentMetadata

A content item within a secret. A single secret can hold multiple content items (e.g., a password file and a certificate), each with version tracking.

| Field | Description |
|-------|-------------|
| ObjectName | Parent secret name |
| ContentName | Unique content identifier |
| FileName | Original file name |
| ContentType | MIME type |
| IsMain | Whether this is the primary content |
| Chunks | List of ChunkMetadata entries |

### ChunkMetadata

References a single encrypted blob chunk in Azure Storage. Large files are split into multiple chunks for streaming.

| Field | Description |
|-------|-------------|
| ChunkName | Unique chunk identifier |
| Hash | Content hash for integrity verification |
| Size | Chunk size in bytes |

### SubjectPermissions

Links a subject (user, group, or application) to a secret with specific permission flags.

| Field | Description |
|-------|-------------|
| SubjectPermissionsId | Unique identifier |
| SecretName | The secret this grant applies to |
| SubjectType | User, Application, or Group |
| SubjectName | UPN, client ID, or group ID |
| CanRead | Read permission flag |
| CanWrite | Write permission flag |
| CanGrantAccess | Grant-access permission flag |
| CanRevokeAccess | Revoke-access permission flag |

### AccessRequest

Tracks a request from a user or application to access a secret they don't currently have permission for.

| Field | Description |
|-------|-------------|
| SecretName | The requested secret |
| RequestorId | Who is requesting access |
| Status | InProgress, Approved, or Denied |
| RequestedPermissions | Which permissions were requested |

### ExpirationMetadata

Defines when a secret should be automatically purged.

| Field | Description |
|-------|-------------|
| ScheduleExpiration | Fixed date/time expiration |
| IdleExpiration | Delete after N hours/days of no access |
| ExpireAt | Computed next-expiration timestamp |

### WebhookSubscription

A registered external endpoint that receives event notifications.

| Field | Description |
|-------|-------------|
| SecretName | The secret to watch |
| WebhookUrl | Target URL for notifications |
| EventType | Which events trigger the webhook |
| AuthHeaders | Authentication headers for the webhook call |

### User

Cached Entra ID user profile.

| Field | Description |
|-------|-------------|
| DisplayName | User's display name from Entra ID |
| UPN | User Principal Name (email-like identifier) |
| Enabled | Whether the user is allowed to use SafeExchange |

### Application

A registered service principal / application identity.

| Field | Description |
|-------|-------------|
| DisplayName | Application name |
| ClientId | Entra ID application (client) ID |
| Enabled | Whether the application is allowed |

## Storage Strategy

- **Cosmos DB** stores all entities above as JSON documents, organized by container with partition keys tuned for common query patterns.
- **Azure Blob Storage** stores the actual encrypted content referenced by ChunkMetadata entries. Blobs are named by chunk ID and are independently encrypted.
- **Azure Queue Storage** holds delayed task messages (e.g., webhook delivery) as transient work items.
