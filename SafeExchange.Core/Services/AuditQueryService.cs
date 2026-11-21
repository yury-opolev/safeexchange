/// <summary>
/// AuditQueryService
/// </summary>

namespace SafeExchange.Core.Services
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Audit;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads a secret's live audit trail, page by page, for callers with Read on the secret.
    /// </summary>
    public sealed class AuditQueryService
    {
        private readonly SafeExchangeDbContext dbContext;

        private readonly IPermissionsManager permissionsManager;

        public AuditQueryService(SafeExchangeDbContext dbContext, IPermissionsManager permissionsManager)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            this.permissionsManager = permissionsManager ?? throw new ArgumentNullException(nameof(permissionsManager));
        }

        public async Task<ServiceResult<SecretAuditPageOutput>> ReadAsync(Caller caller, string secretId, AuditPageQuery query)
        {
            ArgumentNullException.ThrowIfNull(caller);
            ArgumentNullException.ThrowIfNull(query);

            return await ServiceOperation.RunAsync<ServiceResult<SecretAuditPageOutput>>(async () =>
            {
                var metadata = await this.dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
                if (metadata is null)
                {
                    return ServiceResult.NotFound($"Secret '{secretId}' does not exist.");
                }

                if (!(await this.permissionsManager.IsAuthorizedAsync(caller.SubjectType, caller.SubjectId, secretId, PermissionType.Read)))
                {
                    return ServiceResult.InsufficientPermissions(PermissionType.Read, secretId);
                }

                if (!metadata.AuditEnabled || string.IsNullOrEmpty(metadata.AuditInstanceId))
                {
                    return ServiceResult<SecretAuditPageOutput>.Ok(new SecretAuditPageOutput { AuditEnabled = false });
                }

                var page = await BuildAuditPageAsync(this.dbContext, metadata.AuditInstanceId!, query);
                return ServiceResult<SecretAuditPageOutput>.Ok(page);
            });
        }

        /// <summary>
        /// Builds one page of an audit instance. Shared with the admin audit endpoint, which addresses
        /// audit runs by instance id (a secret name can be reused after delete and recreate).
        /// </summary>
        public static async Task<SecretAuditPageOutput> BuildAuditPageAsync(SafeExchangeDbContext dbContext, string auditInstanceId, AuditPageQuery query)
        {
            ArgumentNullException.ThrowIfNull(dbContext);
            ArgumentNullException.ThrowIfNull(query);

            var pageSize = Math.Clamp(query.PageSize, 1, AuditPageQuery.MaxPageSize);
            var after = TryDecodeContinuation(query.Continuation);
            var descending = query.Descending;

            var events = dbContext.SecretAuditEvents.Where(e => e.AuditInstanceId == auditInstanceId);
            if (after.HasValue)
            {
                var afterVal = after.Value;
                events = descending
                    ? events.Where(e => e.SequenceNumber < afterVal)
                    : events.Where(e => e.SequenceNumber > afterVal);
            }

            if (query.From.HasValue)
            {
                var fromVal = query.From.Value;
                events = events.Where(e => e.OccurredAt >= fromVal);
            }

            if (query.To.HasValue)
            {
                var toVal = query.To.Value;
                events = events.Where(e => e.OccurredAt < toVal);
            }

            var ordered = descending
                ? events.OrderByDescending(e => e.SequenceNumber)
                : events.OrderBy(e => e.SequenceNumber);
            var page = await ordered.Take(pageSize + 1).ToListAsync();
            string? nextToken = null;
            if (page.Count > pageSize)
            {
                var lastInPage = page[pageSize - 1];
                nextToken = EncodeContinuation(lastInPage.SequenceNumber);
                page = page.GetRange(0, pageSize);
            }

            // ContentReadMerger expects ascending input. When paging desc, reverse the
            // page in/out so the merger sees ascending and the response preserves desc.
            List<SecretAuditEventOutput> dtoEvents;
            if (descending)
            {
                var ascForMerge = new List<SecretAuditEvent>(page);
                ascForMerge.Reverse();
                var merged = query.Raw ? ContentReadMerger.Raw(ascForMerge) : ContentReadMerger.Merge(ascForMerge);
                merged.Reverse();
                dtoEvents = merged;
            }
            else
            {
                dtoEvents = query.Raw ? ContentReadMerger.Raw(page) : ContentReadMerger.Merge(page);
            }

            return new SecretAuditPageOutput
            {
                AuditEnabled = true,
                Events = dtoEvents,
                NextContinuation = nextToken,
            };
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
