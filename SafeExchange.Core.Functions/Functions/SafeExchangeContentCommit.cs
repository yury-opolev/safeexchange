/// <summary>
/// SafeExchangeContentCommit
/// </summary>

namespace SafeExchange.Core.Functions
{
    using System;
    using System.Linq;
    using System.Security.Claims;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Purger;
    using SafeExchange.Core.Services;
    using SafeExchange.Core.Telemetry;

    /// <summary>
    /// HTTP adapter for <c>PATCH /secret/{secretId}/content/{contentId}/commit</c> over <see cref="ContentService.CommitAsync"/>.
    /// </summary>
    public class SafeExchangeContentCommit
    {
        private readonly ICallerResolver callerResolver;

        private readonly ContentService contentService;

        public SafeExchangeContentCommit(ICallerResolver callerResolver, ContentService contentService)
        {
            this.callerResolver = callerResolver ?? throw new ArgumentNullException(nameof(callerResolver));
            this.contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        }

        /// <summary>
        /// Pre-service constructor, kept for code that builds handlers by hand: resolves callers through
        /// <paramref name="globalFilters"/> exactly as before and builds the service itself (without logging).
        /// </summary>
        public SafeExchangeContentCommit(IConfiguration configuration, SafeExchangeDbContext dbContext,
            ITokenHelper tokenHelper, GlobalFilters globalFilters, IPurger purger, IPermissionsManager permissionsManager, IAuditWriter auditWriter)
            : this(
                  new GlobalFiltersCallerResolver(globalFilters, tokenHelper, dbContext),
                  new ContentService(configuration, dbContext, purger, permissionsManager, auditWriter, NullLogger<ContentService>.Instance))
        {
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, string contentId,
            ClaimsPrincipal principal, ILogger log)
        {
            var resolution = await this.callerResolver.ResolveAsync(principal, CallerChannels.Rest, null);
            if (!resolution.IsAllowed)
            {
                return await resolution.Denial.ToResponseAsync(request);
            }

            var caller = resolution.Caller;
            log.LogInformation($"{nameof(SafeExchangeContentCommit)} triggered for '{secretId}' ({contentId}) by {caller.SubjectType} (tid {TelemetryContext.Current}).");

            return await ActionResults.ServiceCallAsync(request, async () =>
            {
                var ticketHeader = request.Headers.TryGetValues(SafeExchangeSecretStream.AccessTicketHeaderName, out var tickets)
                    ? tickets.FirstOrDefault() ?? string.Empty
                    : string.Empty;

                ContentCommitInput input;
                try
                {
                    var payload = await JsonSerializer.DeserializeAsync<CommitRequest>(request.Body,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    input = new ContentCommitInput(payload?.Hash);
                }
                catch (JsonException)
                {
                    input = ContentCommitInput.Malformed;
                }

                return await (await this.contentService.CommitAsync(caller, secretId, contentId, ticketHeader, input)).ToResponseAsync(request);
            }, nameof(SafeExchangeContentCommit), log);
        }

        private sealed class CommitRequest
        {
            public string? Hash { get; set; }
        }
    }
}
