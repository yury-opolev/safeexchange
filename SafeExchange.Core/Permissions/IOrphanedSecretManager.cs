/// <summary>
/// IOrphanedSecretManager
/// </summary>

namespace SafeExchange.Core.Permissions
{
    using SafeExchange.Core.Model;
    using System.Threading.Tasks;

    public interface IOrphanedSecretManager
    {
        /// <summary>
        /// Computes (without DB writes) what would happen if the orphan rule were applied now.
        /// </summary>
        public Task<OrphanRulePreview> PreviewAsync(string secretId, SafeExchangeDbContext dbContext);

        /// <summary>
        /// Applies the orphan rule. Mutates tracked entities only — does NOT call SaveChangesAsync.
        /// Caller commits as part of its own transaction.
        /// No-ops when Features.UseAccessGiveUp is false.
        /// </summary>
        public Task<OrphanRuleResult> ApplyOrphanRuleAsync(string secretId, SafeExchangeDbContext dbContext);
    }
}
