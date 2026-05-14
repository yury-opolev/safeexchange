/// <summary>
/// IAuditPurger — deletes audit anchors + their event partitions when their
/// retention window expires.
/// </summary>

namespace SafeExchange.Core.Audit
{
    using System.Threading.Tasks;

    public interface IAuditPurger
    {
        Task<int> PurgeExpiredAsync();
    }
}
