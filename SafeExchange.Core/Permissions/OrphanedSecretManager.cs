/// <summary>
/// OrphanedSecretManager
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Model;
    using System;
    using System.Linq;
    using System.Threading.Tasks;

    public class OrphanedSecretManager : IOrphanedSecretManager
    {
        private readonly IOptionsMonitor<Features> features;
        private readonly IOptionsMonitor<OrphanedSecretConfiguration> config;
        private readonly ILogger<OrphanedSecretManager> logger;

        public OrphanedSecretManager(
            IOptionsMonitor<Features> features,
            IOptionsMonitor<OrphanedSecretConfiguration> config,
            ILogger<OrphanedSecretManager> logger)
        {
            this.features = features ?? throw new ArgumentNullException(nameof(features));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<OrphanRulePreview> PreviewAsync(string secretId, SafeExchangeDbContext dbContext)
            => this.PreviewAsync(secretId, dbContext, excludedSubjectType: null, excludedSubjectId: null);

        public Task<OrphanRulePreview> PreviewAsync(string secretId, SafeExchangeDbContext dbContext, SubjectType excludedSubjectType, string excludedSubjectId)
            => this.PreviewAsync(secretId, dbContext, (SubjectType?)excludedSubjectType, excludedSubjectId);

        private async Task<OrphanRulePreview> PreviewAsync(string secretId, SafeExchangeDbContext dbContext, SubjectType? excludedSubjectType, string? excludedSubjectId)
        {
            var hasCustodian = await this.HasCustodianAsync(secretId, dbContext, excludedSubjectType, excludedSubjectId);
            var metadata = await dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));

            DateTime? currentExpireAt = (metadata?.ExpirationMetadata?.ScheduleExpiration ?? false)
                ? metadata.ExpirationMetadata.ExpireAt
                : null;

            if (hasCustodian || metadata == null)
            {
                return new OrphanRulePreview
                {
                    WouldOrphan = false,
                    CurrentExpireAt = currentExpireAt,
                    ProspectiveExpireAt = null
                };
            }

            var prospective = ComputeProspective(metadata.ExpirationMetadata);
            return new OrphanRulePreview
            {
                WouldOrphan = true,
                CurrentExpireAt = currentExpireAt,
                ProspectiveExpireAt = prospective
            };
        }

        public async Task<OrphanRuleResult> ApplyOrphanRuleAsync(string secretId, SafeExchangeDbContext dbContext)
        {
            if (!this.features.CurrentValue.UseAccessGiveUp)
            {
                return new OrphanRuleResult { WasOrphaned = false, ExpireAt = null };
            }

            var hasCustodian = await this.HasCustodianAsync(secretId, dbContext);
            if (hasCustodian)
            {
                this.logger.LogInformation("Secret '{SecretId}' orphan check: still has custodian (no schedule applied).", secretId);
                return new OrphanRuleResult { WasOrphaned = false, ExpireAt = null };
            }

            var metadata = await dbContext.Objects.FirstOrDefaultAsync(o => o.ObjectName.Equals(secretId));
            if (metadata == null)
            {
                return new OrphanRuleResult { WasOrphaned = false, ExpireAt = null };
            }

            var prospective = ComputeProspective(metadata.ExpirationMetadata);

            metadata.ExpirationMetadata.ScheduleExpiration = true;
            metadata.ExpirationMetadata.ExpireAt = prospective;

            this.logger.LogInformation(
                "Secret '{SecretId}' has no custodian after permission change. Scheduled for purge at {ExpireAt}.",
                secretId, prospective);

            return new OrphanRuleResult { WasOrphaned = true, ExpireAt = prospective };
        }

        private DateTime ComputeProspective(ExpirationMetadata metadata)
        {
            var now = DateTimeProvider.UtcNow;
            var grace = now + this.config.CurrentValue.GracePeriod;

            if (metadata.ScheduleExpiration && metadata.ExpireAt <= grace)
            {
                return metadata.ExpireAt;
            }

            return grace;
        }

        private Task<bool> HasCustodianAsync(string secretId, SafeExchangeDbContext dbContext)
            => this.HasCustodianAsync(secretId, dbContext, excludedSubjectType: null, excludedSubjectId: null);

        private async Task<bool> HasCustodianAsync(string secretId, SafeExchangeDbContext dbContext, SubjectType? excludedSubjectType, string? excludedSubjectId)
        {
            var allowGroup = this.config.CurrentValue.Ownership == OrphanOwnershipMode.UserOrAppOrGroup;
            var hasExclusion = excludedSubjectType.HasValue && !string.IsNullOrEmpty(excludedSubjectId);

            bool IsAllowedType(SubjectType t) =>
                t == SubjectType.User
                || t == SubjectType.Application
                || (allowGroup && t == SubjectType.Group);

            bool IsExcluded(SubjectType t, string subjectId) =>
                hasExclusion && t == excludedSubjectType!.Value && subjectId == excludedSubjectId;

            // Reconcile with the change tracker so that callers staging a Remove or
            // a flag flip see the post-save state of custodianship — not the still-uncommitted DB state.
            // Without this, a give-up call that staged a Remove on the last custodian's row would be
            // missed by AnyAsync (which goes to the database, where the row still exists) and the
            // orphan rule would fail to schedule the grace-period purge.

            // Newly-Added rows that would be custodians count immediately (unless excluded).
            var addedCustodian = dbContext.ChangeTracker.Entries<SubjectPermissions>()
                .Any(e => e.State == EntityState.Added
                    && e.Entity.SecretName.Equals(secretId)
                    && e.Entity.CanGrantAccess
                    && IsAllowedType(e.Entity.SubjectType)
                    && !IsExcluded(e.Entity.SubjectType, e.Entity.SubjectId));
            if (addedCustodian)
            {
                return true;
            }

            // Candidate custodians from the DB — apply CanGrantAccess + allowed-type predicate at the source.
            var dbCandidates = await dbContext.Permissions
                .Where(p => p.SecretName.Equals(secretId) && p.CanGrantAccess
                    && (p.SubjectType == SubjectType.User
                        || p.SubjectType == SubjectType.Application
                        || (allowGroup && p.SubjectType == SubjectType.Group)))
                .ToListAsync();

            foreach (var candidate in dbCandidates)
            {
                var entry = dbContext.Entry(candidate);
                if (entry.State == EntityState.Deleted)
                {
                    continue;
                }
                if (entry.State == EntityState.Modified && !candidate.CanGrantAccess)
                {
                    continue;
                }
                if (IsExcluded(candidate.SubjectType, candidate.SubjectId))
                {
                    continue;
                }
                return true;
            }

            return false;
        }
    }
}
