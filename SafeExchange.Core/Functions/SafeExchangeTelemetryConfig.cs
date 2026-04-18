
namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Net;
    using System.Security.Claims;

    /// <summary>
    /// Hands the Application Insights connection string to authenticated
    /// browser clients. Keeping this behind authentication means the
    /// ingestion credential is not extractable from the public
    /// wwwroot/appsettings.json bundle — an attacker needs a valid tenant
    /// JWT before they can exfiltrate it.
    /// </summary>
    public class SafeExchangeTelemetryConfig
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IOptions<WebClientTelemetryConfiguration> telemetryOptions;

        public SafeExchangeTelemetryConfig(
            SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper,
            GlobalFilters globalFilters,
            IOptions<WebClientTelemetryConfiguration> telemetryOptions)
        {
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.telemetryOptions = telemetryOptions ?? throw new ArgumentNullException(nameof(telemetryOptions));
        }

        public async Task<HttpResponseData> Run(
            HttpRequestData request, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResponse) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResponse ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return await ActionResults.ForbiddenAsync(request, "Application is not registered or disabled.");
            }

            log.LogInformation($"{nameof(SafeExchangeTelemetryConfig)} triggered by {subjectType} {subjectId}, [{request.Method}].");

            switch (request.Method.ToLower())
            {
                case "get":
                    return await this.HandleGetConfig(request, log);

                default:
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.BadRequest,
                        new BaseResponseObject<object> { Status = "error", Error = $"Request method '{request.Method}' not allowed." });
            }
        }

        private async Task<HttpResponseData> HandleGetConfig(HttpRequestData request, ILogger log)
            => await ActionResults.TryCatchAsync(request, async () =>
            {
                var connectionString = this.telemetryOptions.Value?.ConnectionString ?? string.Empty;
                var enabled = !string.IsNullOrWhiteSpace(connectionString);

                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<WebClientTelemetryConfigOutput>
                    {
                        Status = "ok",
                        Result = new WebClientTelemetryConfigOutput
                        {
                            Enabled = enabled,
                            ConnectionString = enabled ? connectionString : string.Empty
                        }
                    });
            }, nameof(HandleGetConfig), log);
    }
}
