/// <summary>
/// ActionResults
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using SafeExchange.Core.Permissions;

    public static class ActionResults
    {
        public static IActionResult InsufficientPermissionsResult(PermissionType permission, string secretId)
        {
            return new ObjectResult(new BaseResponseObject<object> { Status = "unauthorized", Error = $"Insufficient permissions to do '{permission}' action on '{secretId}'" }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        public static IActionResult InsufficientPermissionsResult(string actionName, string secretId)
        {
            return new ObjectResult(new BaseResponseObject<object> { Status = "unauthorized", Error = $"Insufficient permissions to do '{actionName}' action on '{secretId}'" }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

    }
}
