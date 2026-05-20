/// <summary>
/// IOrphanedSecretManager
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using System.Threading.Tasks;
    using SafeExchange.Core.Model;

    public interface IOrphanedSecretManager
    {
        /// <summary>
        /// Computes (without DB writes) what would happen if the orphan rule were applied now.
        /// </summary>
        public Task<OrphanRulePreview> PreviewAsync(string secretId, SafeExchangeDbContext dbContext);

        /// <summary>
        /// Computes (without DB writes) what would happen if the caller's direct permission row
        /// were removed and the orphan rule were applied. Used by the give-up preview to answer
        /// "if I give up access right now, would this secret be orphaned?".
        /// </summary>
        public Task<OrphanRulePreview> PreviewAsync(string secretId, SafeExchangeDbContext dbContext, SubjectType excludedSubjectType, string excludedSubjectId);

        /// <summary>
        /// Applies the orphan rule. Mutates tracked entities only — does NOT call SaveChangesAsync.
        /// Caller commits as part of its own transaction.
        /// No-ops when Features.UseAccessGiveUp is false.
        /// </summary>
        public Task<OrphanRuleResult> ApplyOrphanRuleAsync(string secretId, SafeExchangeDbContext dbContext);
    }
}
