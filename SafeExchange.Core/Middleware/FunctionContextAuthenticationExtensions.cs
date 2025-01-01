/// <summary>
/// FunctionContextAuthenticationExtensions
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.Azure.Functions.Worker;
    using SafeExchange.Core.Middleware;
    using System.Security.Claims;

    public static class FunctionContextAuthenticationExtensions
    {
        public static ClaimsPrincipal GetPrincipal(this FunctionContext context)
        {
            return context.Items[DefaultAuthenticationMiddleware.ClaimsPrincipalKey] as ClaimsPrincipal
                ?? throw new ArgumentNullException(DefaultAuthenticationMiddleware.ClaimsPrincipalKey);
        }

        public static string GetUserId(this FunctionContext context)
        {
            return context.Items[DefaultAuthenticationMiddleware.InvocationContextUserIdKey] as string
                ?? throw new ArgumentNullException(DefaultAuthenticationMiddleware.InvocationContextUserIdKey);
        }
    }
}
