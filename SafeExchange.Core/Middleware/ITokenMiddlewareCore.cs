///
/// ITokenMiddlewareCore
///

namespace SafeExchange.Core.Middleware
{
    using Microsoft.Azure.Functions.Worker.Http;
    using System.Security.Claims;
    using System.Threading.Tasks;

    public interface ITokenMiddlewareCore
    {
        public ValueTask<(bool shouldReturn, HttpResponseData? response)> RunAsync(HttpRequestData request, ClaimsPrincipal principal);
    }
}
