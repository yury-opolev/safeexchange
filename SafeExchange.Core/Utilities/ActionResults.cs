/// <summary>
/// ActionResults
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    public static class ActionResults
    {
        public static async Task<HttpResponseData> CreateResponseAsync<T>(HttpRequestData request, HttpStatusCode statusCode, T resultObject)
        {
            var response = request.CreateResponse();
            response.StatusCode = statusCode;
            await response.WriteAsJsonAsync(resultObject);
            return response;
        }

        /// <summary>
        /// Shorthand for the repeating
        /// <c>await CreateResponseAsync(request, HttpStatusCode.Forbidden, new BaseResponseObject&lt;object&gt; { Status = "forbidden", Error = ... })</c>
        /// idiom used by every per-handler "this principal type is not allowed to call
        /// this API" guard.
        ///
        /// Exists so that a missing <c>return</c> before the guard cannot
        /// silently fall through into the main handler body — that exact bug
        /// was the OWASP A01:2025 P0 access-control bypass in
        /// <c>SafeExchangeExternalNotificationDetails</c>. Call sites read as
        /// <c>return await ActionResults.ForbiddenAsync(request, "...");</c>,
        /// and a reviewer missing the <c>return</c> can never have the helper
        /// itself swallow the decision.
        /// </summary>
        public static Task<HttpResponseData> ForbiddenAsync(HttpRequestData request, string error, string subStatus = "")
            => CreateResponseAsync(
                request,
                HttpStatusCode.Forbidden,
                new BaseResponseObject<object> { Status = "forbidden", Error = error, SubStatus = subStatus });

        /// <summary>
        /// Executes <paramref name="action"/> and catches any exception, returning a
        /// generic 500 "Internal error" response whose body contains only a
        /// correlation identifier. The full exception (type, message, stack, inner
        /// exceptions) is logged server-side at <see cref="LogLevel.Error"/> tagged
        /// with the same correlation identifier so operators can join the client-
        /// visible reference to the server log entry.
        ///
        /// This replaces an earlier pattern that echoed <c>ex.GetType()</c> and
        /// <c>ex.Message</c> in the JSON body, which leaked Cosmos / Blob / Graph
        /// internal state, partition keys, container names, framework versions and
        /// other information useful to an attacker (OWASP A02:2025 — CWE-209).
        ///
        /// Keep this helper as the single point where exceptions become
        /// <see cref="HttpResponseData"/> so future hardening (retry classification,
        /// problem-details RFC 7807, etc.) applies to every function handler at once.
        /// </summary>
        public static async Task<HttpResponseData> TryCatchAsync(
            HttpRequestData request,
            Func<Task<HttpResponseData>> action,
            string actionName,
            ILogger log)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                log.LogError(
                    ex,
                    "Exception in {ActionName}, correlationId={CorrelationId}",
                    actionName,
                    correlationId);

                return await CreateResponseAsync(
                    request,
                    HttpStatusCode.InternalServerError,
                    new BaseResponseObject<object>
                    {
                        Status = "error",
                        SubStatus = "internal_exception",
                        Error = $"Internal error. Reference: {correlationId}",
                    });
            }
        }

        public static BaseResponseObject<object> InsufficientPermissions(PermissionType permission, string secretId, string subStatus)
        {
            return new BaseResponseObject<object>
            {
                Status = "forbidden",
                Error = $"Insufficient permissions to do '{permission}' action on '{secretId}'",
                SubStatus = subStatus
            };
        }

        public static BaseResponseObject<object> InsufficientPermissions(string actionName, string secretId, string subStatus)
        {
            return new BaseResponseObject<object>
            {
                Status = "forbidden",
                Error = $"Insufficient permissions to do '{actionName}' action on '{secretId}'",
                SubStatus = subStatus
            };
        }

        public static BaseResponseObject<object> InsufficientPermissions(string error, string subStatus)
        {
            return new BaseResponseObject<object>
            {
                Status = "forbidden",
                Error = error,
                SubStatus = subStatus
            };
        }
    }
}
