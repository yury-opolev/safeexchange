/// <summary>
/// ApplicationOwner — a principal (user UPN or group OID) that owns an
/// Application. Many-to-many: an app has multiple owners; a principal can own
/// multiple apps. See docs/SPIKE-s2s-apps.md for the ownership invariant.
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public class ApplicationOwner
    {
        public const string PartitionKeyPrefix = "OWNERS-";

        public ApplicationOwner() { }

        public ApplicationOwner(string applicationId, OwnerSubjectType subjectType, string subjectId, string addedBy, string subjectName = "")
        {
            if (string.IsNullOrWhiteSpace(applicationId))
            {
                throw new ArgumentException("Application id is required.", nameof(applicationId));
            }
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                throw new ArgumentException("Subject id is required.", nameof(subjectId));
            }

            this.ApplicationId = applicationId;
            this.PartitionKey = $"{PartitionKeyPrefix}{applicationId}";
            this.SubjectType = subjectType;
            this.SubjectId = subjectId;
            this.SubjectName = subjectName ?? string.Empty;
            // Composite id keeps owner rows deterministic so re-adding a removed owner
            // doesn't create a duplicate, and "is X an owner" can hit a single point read.
            this.Id = $"{(int)subjectType}:{subjectId}";
            this.AddedAt = DateTimeProvider.UtcNow;
            this.AddedBy = addedBy ?? string.Empty;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public string ApplicationId { get; set; }

        public OwnerSubjectType SubjectType { get; set; }

        public string SubjectId { get; set; }

        /// <summary>Friendly label captured from the directory picker. May be empty for older rows.</summary>
        public string SubjectName { get; set; } = string.Empty;

        public DateTime AddedAt { get; set; }

        public string AddedBy { get; set; }
    }
}
