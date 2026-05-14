# P0 — Fix Immediately (before next deploy)

> See [`SUMMARY.md`](SUMMARY.md) for methodology and rubric.

## [P0] [CRITICAL] Missing `return` lets any authenticated user reach application-only notification endpoint

- **Category:** A01:2025 — Broken Access Control (root cause also A10 — fail-open on access check)
- **CWE:** CWE-862 (Missing Authorization), CWE-670 (Always-Incorrect Control Flow Implementation), CWE-755 (Improper Exceptional Condition Handling)
- **File:** `SafeExchange.Core/Functions/SafeExchangeExternalNotificationDetails.cs:50-70` (handler entry), `:86-148` (data disclosure + destructive purge)
- **Severity:** Critical
- **Exploitability:** Trivial
- **Exposure:** Authenticated-user (any AAD user accepted by the middleware)
- **Confidence:** Confirmed
- **Priority:** **P0**

### Description

`SafeExchangeExternalNotificationDetails.Run` has three stacked guards that each build a 403 `HttpResponseData` and then silently discard it — **no `return` statement** is present after any of them. Execution therefore falls straight through to the `switch` / `HandleExternalNotificationDetailsRead` call for **every authenticated caller**, not just registered applications with `ExternalNotificationsReader = true`.

### Evidence

```csharp
// SafeExchangeExternalNotificationDetails.cs:50-70
if (!SubjectType.Application.Equals(subjectType))
{
    await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.Forbidden,
        new BaseResponseObject<object> { Status = "forbidden", Error = "Only applications can use this API." });
    // ← MISSING `return response;`
}

if (string.IsNullOrEmpty(subjectId))
{
    await ActionResults.CreateResponseAsync(...);           // ← MISSING return
}

var application = this.dbContext.Applications.FirstOrDefault(a => a.DisplayName.Equals(subjectId));
if (application?.ExternalNotificationsReader != true)
{
    await ActionResults.CreateResponseAsync(...);           // ← MISSING return
}

// falls through to:
switch (request.Method.ToLower())
{
    case "get": return await this.HandleExternalNotificationDetailsRead(...);
}
```

`HandleExternalNotificationDetailsRead` then:

1. Calls `purger.PurgeNotificationDataIfNeededAsync(webhookNotificationDataId, ...)` — **destructive side effect triggered by any authenticated caller**.
2. Reads the raw `WebhookNotificationData`, resolves the linked `AccessRequest`, and returns a `NotificationDataOutput` whose `RecipientUpns` field contains the UPN list of every user authorized to approve access to the target secret.
3. Calls `purger.PurgeNotificationDataAsync(webhookNotificationDataId, ...)` — **deletes the record** after returning.

### Attack scenario

1. Attacker obtains any AAD user token accepted by `DefaultAuthenticationMiddleware`. On the default configuration where `ValidateIssuer=false` / `ValidateAudience=false` (see P1 finding `P1-high.md`), this is **any account in any Entra tenant**. Under a tightened configuration, still any user in the allowed tenant.
2. Attacker enumerates `webhookNotificationDataId` values (they are GUIDs, but the attacker only needs the ones broadcast in response to their own access requests, leaked via logs, or brute-forced if the system has many active notifications).
3. `GET /api/external-notifications/{webhookNotificationDataId}` returns `{ Url, RecipientUpns: [...] }` — the attacker now has a roster of privileged approvers for a specific secret, usable for targeted phishing / social engineering / account takeover planning.
4. Concurrently, the endpoint purges the notification data, **blocking the legitimate downstream webhook delivery** for that access request. At scale, any authenticated user can tombstone every in-progress access request in the tenant, causing a denial-of-service against the approval workflow.

### Recommendation

```csharp
if (!SubjectType.Application.Equals(subjectType))
{
    return await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.Forbidden,
        new BaseResponseObject<object> { Status = "forbidden", Error = "Only applications can use this API." });
}
// … same `return` for the next two guards
```

**How to verify the fix:** send a user JWT to `GET /api/external-notifications/<any-guid>` and confirm 403 is returned **before any DB access**. Add an integration test that asserts `HandleExternalNotificationDetailsRead` is not reached with a non-application principal. Also asserts no `WebhookNotificationData` row is deleted from the test DB during the attempted call.

### Related findings

- [`P4-informational.md` → Missing-return pattern in 8 defensive guards](P4-informational.md) — the same bug exists in 8 other locations (`SafeExchangeAdminGroups.cs:53`, `SafeExchangeAdminOperations.cs:49`, `SafeExchangeApplications.cs:49`, etc.), but those guards are currently defensive-only because `TokenMiddlewareCore` blocks unregistered apps upstream. Fix them all in the same commit to avoid confusion later.

### References

- <https://owasp.org/Top10/2025/A01_2025-Broken_Access_Control/>
- <https://cwe.mitre.org/data/definitions/862.html>
- <https://cwe.mitre.org/data/definitions/670.html>
- <https://cwe.mitre.org/data/definitions/755.html>
