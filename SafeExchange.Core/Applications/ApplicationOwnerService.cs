/// <summary>
/// ApplicationOwnerService — implementation of <see cref="IApplicationOwnerService"/>.
/// EF-backed; takes a DbContext and produces deterministic owner rows.
/// </summary>

namespace SafeExchange.Core.Applications
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class ApplicationOwnerService : IApplicationOwnerService
    {
        private readonly SafeExchangeDbContext dbContext;

        public ApplicationOwnerService(SafeExchangeDbContext dbContext)
        {
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public void ValidateInvariant(IReadOnlyCollection<ApplicationOwner> owners)
        {
            if (owners is null)
            {
                throw new ArgumentNullException(nameof(owners));
            }

            // Distinct by (type, id) — defensive: callers shouldn't pass duplicates
            // but the EF key prevents them anyway, so this is a belt-and-braces check.
            var distinct = owners
                .Select(o => (o.SubjectType, o.SubjectId))
                .Distinct()
                .ToList();

            if (distinct.Count < 2)
            {
                throw new ApplicationOwnerInvariantException(
                    "An application must have at least two distinct owner principals.");
            }

            if (!owners.Any(o => o.SubjectType == OwnerSubjectType.User))
            {
                throw new ApplicationOwnerInvariantException(
                    "An application must have at least one User owner (groups alone are not sufficient).");
            }
        }

        public Task<List<ApplicationOwner>> ListOwnersAsync(string applicationId, CancellationToken ct = default)
        {
            return this.dbContext.ApplicationOwners
                .Where(o => o.ApplicationId == applicationId)
                .ToListAsync(ct);
        }

        public async Task AddOwnerAsync(string applicationId, OwnerSubjectType subjectType, string subjectId, string addedBy, CancellationToken ct = default)
        {
            // Idempotent: re-adding a same-typed same-id owner is a no-op so
            // the UI doesn't have to know whether a principal is already an owner.
            var existing = await this.dbContext.ApplicationOwners.FindAsync(
                new object[] { applicationId, subjectType, subjectId }, ct);
            if (existing is not null)
            {
                return;
            }

            var row = new ApplicationOwner(applicationId, subjectType, subjectId, addedBy);
            await this.dbContext.ApplicationOwners.AddAsync(row, ct);
            await this.dbContext.SaveChangesAsync(ct);
        }

        public async Task RemoveOwnerAsync(string applicationId, OwnerSubjectType subjectType, string subjectId, CancellationToken ct = default)
        {
            var existing = await this.dbContext.ApplicationOwners.FindAsync(
                new object[] { applicationId, subjectType, subjectId }, ct);
            if (existing is null)
            {
                return;
            }

            // Validate that the *resulting* owner set still satisfies the invariant.
            // Read-then-write race is acceptable for the spike; see SPIKE doc for
            // the optimistic-concurrency follow-up note.
            var remaining = await this.dbContext.ApplicationOwners
                .Where(o => o.ApplicationId == applicationId
                            && !(o.SubjectType == subjectType && o.SubjectId == subjectId))
                .ToListAsync(ct);

            this.ValidateInvariant(remaining);

            this.dbContext.ApplicationOwners.Remove(existing);
            await this.dbContext.SaveChangesAsync(ct);
        }

        public async Task<bool> IsOwnerAsync(string applicationId, OwnerSubjectType subjectType, string subjectId, CancellationToken ct = default)
        {
            // CountAsync(...) > 0 instead of AnyAsync(predicate): the latter generates
            // SELECT VALUE EXISTS(...) which the Cosmos emulator doesn't support.
            var matches = await this.dbContext.ApplicationOwners
                .CountAsync(o => o.ApplicationId == applicationId
                              && o.SubjectType == subjectType
                              && o.SubjectId == subjectId, ct);
            return matches > 0;
        }

        public async Task<List<Application>> ListAppsOwnedByUserAsync(string upn, CancellationToken ct = default)
        {
            var ownedAppIds = await this.dbContext.ApplicationOwners
                .Where(o => o.SubjectType == OwnerSubjectType.User && o.SubjectId == upn)
                .Select(o => o.ApplicationId)
                .ToListAsync(ct);

            if (ownedAppIds.Count == 0)
            {
                return new List<Application>();
            }

            return await this.dbContext.Applications
                .Where(a => ownedAppIds.Contains(a.Id))
                .ToListAsync(ct);
        }
    }
}
