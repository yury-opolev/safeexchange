/// <summary>
/// ApplicationSearchOutput — a single hit from POST /v2/application-search. Carries
/// the client/tenant id alongside the display name so the access picker can show
/// disambiguating detail for multi-tenant apps that share a similar name.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    public class ApplicationSearchOutput
    {
        public string DisplayName { get; set; }

        public string AadClientId { get; set; }

        public string AadTenantId { get; set; }
    }
}
