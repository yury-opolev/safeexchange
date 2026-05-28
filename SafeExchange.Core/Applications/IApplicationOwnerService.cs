/// <summary>
/// IApplicationOwnerService — single source of truth for the ApplicationOwner
/// invariant and owner-set queries. Kept narrow so it's trivial to fake/mock
/// in tests for the endpoint layer.
/// </summary>

namespace SafeExchange.Core.Applications
{
    using SafeExchange.Core.Model;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IApplicationOwnerService
    {
        /// <summary>
        /// Throw <see cref="ApplicationOwnerInvariantException"/> unless the
        /// proposed owner set has >=2 distinct principals AND >=1 of them is
        /// a User. Pure / no I/O — safe to call inside a SaveChanges block.
        /// </summary>
        void ValidateInvariant(IReadOnlyCollection<ApplicationOwner> owners);

        Task<List<ApplicationOwner>> ListOwnersAsync(string applicationId, CancellationToken ct = default);

        Task AddOwnerAsync(string applicationId, OwnerSubjectType subjectType, string subjectId, string addedBy, string subjectName = "", CancellationToken ct = default);

        Task RemoveOwnerAsync(string applicationId, OwnerSubjectType subjectType, string subjectId, CancellationToken ct = default);

        /// <summary>
        /// Reconcile the owner set for one application in a single transaction
        /// (one SaveChanges within the OWNERS-{appId} partition). Validates the
        /// invariant against the desired state first, then commits adds/removes
        /// together. Names on existing rows are refreshed if the desired entry
        /// carries one and the persisted row didn't.
        /// </summary>
        Task ReplaceOwnersAsync(string applicationId, IReadOnlyList<ApplicationOwner> desired, CancellationToken ct = default);

        Task<bool> IsOwnerAsync(string applicationId, OwnerSubjectType subjectType, string subjectId, CancellationToken ct = default);

        /// <summary>
        /// Apps directly owned by the given user UPN. Group-based ownership is
        /// resolved separately at the endpoint layer using GroupDictionary —
        /// keeping the service free of that dependency.
        /// </summary>
        Task<List<Application>> ListAppsOwnedByUserAsync(string upn, CancellationToken ct = default);
    }
}
