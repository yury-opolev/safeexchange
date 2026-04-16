# Authentication and Authorization

This document describes how SafeExchange authenticates callers and enforces access control.

## Authentication

SafeExchange uses Microsoft Entra ID (formerly Azure AD) for authentication. Every API request must include a valid JWT Bearer token in the `Authorization` header.

### Token Validation

The authentication middleware (`DefaultAuthenticationMiddleware`) validates each incoming token by:

1. Fetching the OpenID Connect metadata and signing keys from the configured Entra ID tenant.
2. Verifying the token signature against Azure AD's public keys.
3. Checking standard claims: issuer, audience, and expiration.
4. Extracting the caller's identity (UPN for users, client ID for applications).

If validation fails, the request is rejected with a `401 Unauthorized` response before reaching any business logic.

### Subject Types

SafeExchange recognizes two types of authenticated callers:

| Subject Type | Identified By | Source |
|-------------|---------------|--------|
| **User** | UPN (User Principal Name) | Token `upn` or `preferred_username` claim |
| **Application** | Client ID | Token `appid` or `azp` claim |

Applications must be pre-registered in SafeExchange before they can access the API. Users are automatically recognized if they belong to the configured Entra ID tenant.

## Authorization

After authentication, every operation goes through the permissions engine to determine if the caller is allowed to perform the requested action.

### Permission Types

Each permission grant includes one or more of the following flags:

| Permission | Allows |
|-----------|--------|
| **Read** | View secret metadata, download content and chunks |
| **Write** | Modify secret metadata, upload new content or chunks |
| **GrantAccess** | Grant permissions to other users or groups |
| **RevokeAccess** | Revoke permissions from other users or groups |

The secret creator automatically receives all four permissions.

### Permission Resolution

When a request arrives, the `PermissionsManager` evaluates access in this order:

```
1. Direct Permission Check
   └─ Look up SubjectPermissions for (subjectId, secretId)
   └─ If found → use these permissions

2. Group Permission Check (if no direct match)
   └─ Extract group memberships from the user's token
   └─ For each group, check SubjectPermissions for (groupId, secretId)
   └─ If any group has the required permission → allow

3. Global Filters
   └─ If "globally allowed groups" are configured, verify the user belongs to at least one
   └─ Check that the user/application is not disabled or pending consent
```

### Group-Based Access

SafeExchange supports granting permissions to Entra ID security groups. When a group is granted access:

- All members of that group inherit the granted permissions.
- Group membership is resolved via Microsoft Graph API.
- Nested group membership is supported (groups within groups).
- The system caches group information to minimize Graph API calls.

### Access Request Workflow

Users who do not have permission to a secret can request access:

1. The requester creates an `AccessRequest` for a specific secret.
2. The secret owner (or anyone with `GrantAccess` permission) is notified.
3. The owner approves or denies the request.
4. If approved, the appropriate `SubjectPermissions` entry is created.

Access requests track their state (`InProgress`, `Approved`, `Denied`) and maintain an audit trail of who acted on them.
