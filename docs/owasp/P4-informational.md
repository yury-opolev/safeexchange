# P4 — Informational

> See [`SUMMARY.md`](SUMMARY.md) for methodology and rubric.

These entries are best-practice deviations with no direct impact. Document and close.

## [P4] [INFO] Missing-`return` pattern in 8 defensive guards

- **Category:** A01:2025 (latent) / A10:2025 (fail-open exceptional-case handling)
- **CWE:** CWE-670 (Always-Incorrect Control Flow Implementation)
- **Files:**
  - `SafeExchange.Core/Functions/Admin/SafeExchangeAdminGroups.cs:53`
  - `SafeExchange.Core/Functions/Admin/SafeExchangeAdminOperations.cs:49`
  - `SafeExchange.Core/Functions/Admin/SafeExchangeApplications.cs:49`
  - `SafeExchange.Core/Functions/Admin/SafeExchangeWebhookSubscriptions.cs:47`
  - `SafeExchange.Core/Functions/Admin/SafeExchangeWebhookSubscriptionsList.cs:39`
  - `SafeExchange.Core/Functions/SafeExchangeAccess.cs:57`
  - `SafeExchange.Core/Functions/SafeExchangeAccessRequest.cs:66,113`
  - `SafeExchange.Core/Functions/SafeExchangeApplicationsList.cs:39`
  - `SafeExchange.Core/Functions/SafeExchangeSecretStream.cs:81,117`
- **Severity:** Info · **Exploitability:** Theoretical · **Exposure:** Authenticated-user · **Confidence:** High · **Priority:** P4

### Description

These guards share the same `await ActionResults.CreateResponseAsync(..., Forbidden, ...);` pattern with no `return` statement after the call — identical to the [P0 critical bug](P0-critical.md) in `SafeExchangeExternalNotificationDetails.Run`. They are currently **defensive-only** because `TokenMiddlewareCore` blocks unregistered applications upstream (returns 403 from middleware), so these per-handler guards are never reached in practice.

They become load-bearing the moment the middleware changes: a refactor that weakens the global application registration check, a new endpoint that bypasses the middleware, or a TOCTOU between the middleware check and the handler would immediately promote each of these to the same severity as the P0.

### Evidence (representative)

```csharp
// SafeExchangeAdminOperations.cs:49-54
(SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(...);
if (SubjectType.Application.Equals(subjectType))
{
    await ActionResults.CreateResponseAsync(
        request, HttpStatusCode.Forbidden,
        new BaseResponseObject<object> { Status = "forbidden", Error = "Applications cannot use this API." });
    // ← MISSING `return`
}

log.LogInformation(...);    // fall-through
switch (request.Method.ToLower()) { ... }
```

### Recommendation

Fix all instances in the same commit that fixes the P0. Suggest two mechanical changes:

1. **Add `return`** to every hit:
   ```csharp
   if (SubjectType.Application.Equals(subjectType))
   {
       return await ActionResults.CreateResponseAsync(
           request, HttpStatusCode.Forbidden,
           new BaseResponseObject<object> { Status = "forbidden", Error = "Applications cannot use this API." });
   }
   ```
2. **Introduce a helper** that makes it structurally impossible to forget the return:
   ```csharp
   // In ActionResults.cs
   public static async Task<HttpResponseData> ForbiddenAsync(HttpRequestData request, string error)
       => await CreateResponseAsync(
           request, HttpStatusCode.Forbidden,
           new BaseResponseObject<object> { Status = "forbidden", Error = error });

   // Call sites read more naturally:
   if (SubjectType.Application.Equals(subjectType))
   {
       return await ActionResults.ForbiddenAsync(request, "Applications cannot use this API.");
   }
   ```
3. **Add a Roslyn analyzer** that flags unused `HttpResponseData` locals returned from `ActionResults.Create*` / `ActionResults.Forbidden*`. Pattern: any expression of type `Task<HttpResponseData>` whose result is awaited but discarded inside an `if`-statement body should be a build-time warning-as-error. A single analyzer prevents this bug class from returning.

### References

- <https://cwe.mitre.org/data/definitions/670.html>
- <https://owasp.org/Top10/2025/A01_2025-Broken_Access_Control/>
- <https://owasp.org/Top10/2025/A10_2025-Mishandling_of_Exceptional_Conditions/>
