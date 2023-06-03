/// <summary>
/// AccessRequestOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class ApplicationRegistrationOutput
    {
        public string DisplayName { get; set; }

        public string AadTenantId { get; set; }

        public string AadClientId { get; set; }

        public string ContactEmail { get; set; }

        public bool Enabled { get; set; }
    }
}
