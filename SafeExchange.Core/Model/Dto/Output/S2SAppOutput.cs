/// <summary>
/// S2SAppOutput — detail view returned by GET /s2sapps/{name} and POST /s2sapps.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;
    using System.Collections.Generic;

    public class S2SAppOutput
    {
        public string DisplayName { get; set; }

        public string AadTenantId { get; set; }

        public string AadClientId { get; set; }

        public string ContactEmail { get; set; }

        public bool Enabled { get; set; }

        public DateTime CreatedAt { get; set; }

        public List<S2SAppOwnerOutput> Owners { get; set; } = new();
    }

    public class S2SAppOwnerOutput
    {
        public OwnerSubjectType SubjectType { get; set; }

        public string SubjectId { get; set; }

        public DateTime AddedAt { get; set; }
    }

    public class S2SAppOverviewOutput
    {
        public string DisplayName { get; set; }

        public string AadClientId { get; set; }

        public bool Enabled { get; set; }

        public int OwnerCount { get; set; }

        /// <summary>
        /// True iff the caller is the registrar (primary owner) of this app.
        /// False = the caller was added later as a co-owner. Lets the UI
        /// distinguish the two roles without an extra round-trip.
        /// </summary>
        public bool IsRegistrar { get; set; }
    }
}
