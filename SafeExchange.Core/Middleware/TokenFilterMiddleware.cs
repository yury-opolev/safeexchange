/// <summary>
/// TokenFilterMiddleware
/// </summary>

namespace SafeExchange.Core.Middleware
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker.Middleware;
    using Microsoft.Extensions.Logging;
    using System.Net;
    using System.Security.Claims;

    public class TokenFilterMiddleware : IFunctionsWorkerMiddleware
    {
        private readonly ITokenMiddlewareCore middlewareCore;

        private readonly ILogger log;

        public TokenFilterMiddleware(ITokenMiddlewareCore middlewareCore, ILogger<TokenFilterMiddleware> log)
        {
            this.middlewareCore = middlewareCore ?? throw new ArgumentNullException(nameof(middlewareCore));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            var httpRequestData = await context.GetHttpRequestDataAsync();
            var principal = context.Items[DefaultAuthenticationMiddleware.ClaimsPrincipalKey] as ClaimsPrincipal;
            if (principal == default)
            {
                this.log.LogWarning($"There is no claims principal in {nameof(context.Items)}.");
                await UnauthorizedAsync(context, httpRequestData, "Bearer token is not present or invalid.");
                return;
            }

            (bool shouldReturn, HttpResponseData? earlyResponse) = await this.middlewareCore.RunAsync(httpRequestData!, principal);
            if (shouldReturn)
            {
                SetResponse(context, earlyResponse);
                return;
            }

            await next(context);
        }

        private static async Task UnauthorizedAsync(FunctionContext context, HttpRequestData? httpRequestData, string errorMessage)
        {
            var response = httpRequestData!.CreateResponse();
            await response.WriteAsJsonAsync(new BaseResponseObject<object> { Status = "unauthorized", Error = errorMessage });
            response.StatusCode = HttpStatusCode.Unauthorized;

            SetResponse(context, response);
        }

        private static void SetResponse(FunctionContext context, HttpResponseData? response)
        {
            context.GetInvocationResult().Value = response;
        }
    }
}
