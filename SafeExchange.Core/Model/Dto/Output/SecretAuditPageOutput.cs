/// <summary>
/// SecretAuditPageOutput — paged audit response for GET /v2/secret/{id}/audit.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System.Collections.Generic;

    public class SecretAuditPageOutput
    {
        public bool AuditEnabled { get; set; }

        public List<SecretAuditEventOutput> Events { get; set; } = new();

        public string? NextContinuation { get; set; }
    }
}
