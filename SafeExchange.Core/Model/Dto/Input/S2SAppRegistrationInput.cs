/// <summary>
/// S2SAppRegistrationInput — body of POST /s2sapps (self-service register).
/// Caller becomes owner #1 automatically; this DTO carries the rest of the
/// registration data + the additional owner(s) needed to satisfy the
/// invariant.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System.Collections.Generic;

    public class S2SAppRegistrationInput
    {
        /// <summary>Display name (path id). Letters/digits/hyphens/spaces.</summary>
        public string DisplayName { get; set; }

        /// <summary>Entra application (client) id GUID.</summary>
        public string AadClientId { get; set; }

        /// <summary>
        /// Optional. Entra tenant id GUID. If empty, defaults server-side to
        /// the caller's home tenant from the token.
        /// </summary>
        public string AadTenantId { get; set; }

        /// <summary>
        /// Optional. Contact email. If empty, defaults server-side to the
        /// caller's UPN.
        /// </summary>
        public string ContactEmail { get; set; }

        /// <summary>
        /// Owners to add **in addition to** the caller. Must include at least
        /// one entry to satisfy the >=2 invariant (or >=1 user + >=1 group
        /// when the second owner is a Group).
        /// </summary>
        public List<S2SAppOwnerInput> AdditionalOwners { get; set; } = new();
    }

    public class S2SAppOwnerInput
    {
        public OwnerSubjectType SubjectType { get; set; }

        /// <summary>UPN for users, OID for groups.</summary>
        public string SubjectId { get; set; }

        /// <summary>
        /// Optional friendly label captured from the directory picker (group
        /// display name, user display name). Persisted alongside SubjectId so
        /// the UI can render something readable without resolving the GUID.
        /// </summary>
        public string SubjectName { get; set; }
    }
}
