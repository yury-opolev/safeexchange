/// <summary>
/// ApplicationRegistrationInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class ApplicationRegistrationInput
    {
        public bool Enabled { get; set; } = true;

        public string AadTenantId { get; set; }

        public string AadClientId { get; set; }

        public string ContactEmail { get; set; }
    }
}
