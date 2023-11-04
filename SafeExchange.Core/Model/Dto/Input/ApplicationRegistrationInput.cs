/// <summary>
/// ApplicationRegistrationInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class ApplicationRegistrationInput
    {
        public bool Enabled { get; set; } = true;

        public bool ExternalNotificationsReader { get; set; } = false;

        public string AadTenantId { get; set; }

        public string AadClientId { get; set; }

        public string ContactEmail { get; set; }
    }
}
