/// <summary>
/// S2SAllowedTenantOutput — a tenant the operator permits for S2S app registration,
/// returned by GET /v2/s2sapps-allowed-tenants to populate the registration tenant
/// picker. DisplayName is the operator-supplied label (falls back to the tenant id).
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    public class S2SAllowedTenantOutput
    {
        public string TenantId { get; set; }

        public string DisplayName { get; set; }
    }
}
