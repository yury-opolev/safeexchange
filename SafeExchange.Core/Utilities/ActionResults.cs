/// <summary>
/// ActionResults
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Permissions;
    using System.Net;

    public static class ActionResults
    {
        public static async Task<HttpResponseData> CreateResponseAsync<T>(HttpRequestData request, HttpStatusCode statusCode, T resultObject)
        {
            var response = request.CreateResponse();
            await response.WriteAsJsonAsync(resultObject);
            response.StatusCode = statusCode;
            return response;
        }

        public static BaseResponseObject<object> InsufficientPermissions(PermissionType permission, string secretId, string subStatus)
        {
            return new BaseResponseObject<object>
            {
                Status = "unauthorized",
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

    }
}
