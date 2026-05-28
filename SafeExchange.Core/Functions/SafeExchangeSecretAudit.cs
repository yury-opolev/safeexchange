/// <summary>
/// SafeExchangeSecretAudit — GET /v2/secret/{secretId}/audit handler.
/// Returns the live audit instance's events for callers with Read on the secret.
/// </summary>

namespace SafeExchange.Core.Functions
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Filters;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Threading.Tasks;

    public class SafeExchangeSecretAudit
    {
        private const int DefaultPageSize = 100;

        private const int MaxPageSize = 500;

        private readonly SafeExchangeDbContext dbContext;

        private readonly ITokenHelper tokenHelper;

        private readonly GlobalFilters globalFilters;

        private readonly IPermissionsManager permissionsManager;

        public SafeExchangeSecretAudit(SafeExchangeDbContext dbContext, ITokenHelper tokenHelper, GlobalFilters globalFilters, IPermissionsManager permissionsManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.tokenHelper = tokenHelper ?? throw new ArgumentNullException(nameof(tokenHelper));
            this.globalFilters = globalFilters ?? throw new ArgumentNullException(nameof(globalFilters));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<HttpResponseData> Run(HttpRequestData request, string secretId, ClaimsPrincipal principal, ILogger log)
        {
            var (shouldReturn, filterResult) = await this.globalFilters.GetFilterResultAsync(request, principal, this.dbContext);
            if (shouldReturn)
            {
                return filterResult ?? request.CreateResponse(HttpStatusCode.NoContent);
            }

            (SubjectType subjectType, string subjectId) = await SubjectHelper.GetSubjectInfoAsync(this.tokenHelper, principal, this.dbContext);
            if (SubjectType.Application.Equals(subjectType) && string.IsNullOrEmpty(subjectId))
            {
                return await ActionResults.ForbiddenAsync(request, "Application is not registered or disabled.");
            }

            log.LogInformation($"{nameof(SafeExchangeSecretAudit)} triggered for '{secretId}' by {subjectType} {subjectId}.");

            return await ActionResults.TryCatchAsync(request, async () =>
            {
                var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (metadata is null)
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.NotFound,
                        new BaseResponseObject<object> { Status = "not_found", Error = $"Secret '{secretId}' does not exist." });
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(subjectType, subjectId, secretId, PermissionType.Read)))
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.Forbidden,
                        ActionResults.InsufficientPermissions(PermissionType.Read, secretId, string.Empty));
                }

                if (!metadata.AuditEnabled || string.IsNullOrEmpty(metadata.AuditInstanceId))
                {
                    return await ActionResults.CreateResponseAsync(
                        request, HttpStatusCode.OK,
                        new BaseResponseObject<SecretAuditPageOutput>
                        {
                            Status = "ok",
                            Result = new SecretAuditPageOutput { AuditEnabled = false },
                        });
                }

                var page = await BuildAuditPageAsync(this.dbContext, metadata.AuditInstanceId!, request.Url?.Query ?? string.Empty);
                return await ActionResults.CreateResponseAsync(
                    request, HttpStatusCode.OK,
                    new BaseResponseObject<SecretAuditPageOutput> { Status = "ok", Result = page });
            }, nameof(SafeExchangeSecretAudit), log);
        }

        // Shared with SafeExchangeAdminSecretAudit. Takes an auditInstanceId
        // directly so the admin path can be route-keyed by instance (the
        // immutable identity of an audit run) rather than by SecretObjectName
        // (which can be reused after delete+recreate). queryString is the raw
        // request URL query — decoupling from HttpRequestData keeps the helper
        // unit-testable without an HTTP request fixture.
        internal static async Task<SecretAuditPageOutput> BuildAuditPageAsync(
            SafeExchangeDbContext dbContext, string auditInstanceId, string queryString)
        {
            var qs = System.Web.HttpUtility.ParseQueryString(queryString ?? string.Empty);
            var from = TryParseUtc(qs["from"]);
            var to = TryParseUtc(qs["to"]);
            var raw = bool.TryParse(qs["raw"], out var r) && r;
            var pageSize = int.TryParse(qs["pageSize"], out var p) ? Math.Clamp(p, 1, MaxPageSize) : DefaultPageSize;
            var after = TryDecodeContinuation(qs["continuation"]);
            var descending = string.Equals(qs["direction"], "desc", StringComparison.OrdinalIgnoreCase);

            var query = dbContext.SecretAuditEvents.Where(e => e.AuditInstanceId == auditInstanceId);
            if (after.HasValue)
            {
                var afterVal = after.Value;
                query = descending
                    ? query.Where(e => e.SequenceNumber < afterVal)
                    : query.Where(e => e.SequenceNumber > afterVal);
            }

            if (from.HasValue)
            {
                var fromVal = from.Value;
                query = query.Where(e => e.OccurredAt >= fromVal);
            }

            if (to.HasValue)
            {
                var toVal = to.Value;
                query = query.Where(e => e.OccurredAt < toVal);
            }

            var ordered = descending
                ? query.OrderByDescending(e => e.SequenceNumber)
                : query.OrderBy(e => e.SequenceNumber);
            var events = await ordered.Take(pageSize + 1).ToListAsync();
            string? nextToken = null;
            if (events.Count > pageSize)
            {
                var lastInPage = events[pageSize - 1];
                nextToken = EncodeContinuation(lastInPage.SequenceNumber);
                events = events.GetRange(0, pageSize);
            }

            // ContentReadMerger expects ascending input. When paging desc, reverse the
            // page in/out so the merger sees ascending and the response preserves desc.
            List<SecretAuditEventOutput> dtoEvents;
            if (descending)
            {
                var ascForMerge = new List<SecretAuditEvent>(events);
                ascForMerge.Reverse();
                var merged = raw ? ContentReadMerger.Raw(ascForMerge) : ContentReadMerger.Merge(ascForMerge);
                merged.Reverse();
                dtoEvents = merged;
            }
            else
            {
                dtoEvents = raw ? ContentReadMerger.Raw(events) : ContentReadMerger.Merge(events);
            }

            return new SecretAuditPageOutput
            {
                AuditEnabled = true,
                Events = dtoEvents,
                NextContinuation = nextToken,
            };
        }

        private static DateTime? TryParseUtc(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt) ? dt : null;
        }

        private static long? TryDecodeContinuation(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }
            try
            {
                var bytes = Convert.FromBase64String(token);
                var s = Encoding.UTF8.GetString(bytes);
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static string EncodeContinuation(long sequenceNumber)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(sequenceNumber.ToString(CultureInfo.InvariantCulture)));
    }
}
