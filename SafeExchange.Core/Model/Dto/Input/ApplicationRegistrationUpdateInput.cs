/// <summary>
/// ApplicationRegistrationUpdateInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class ApplicationRegistrationUpdateInput
    {
        public bool? Enabled { get; set; }

        public bool? ExternalNotificationsReader { get; set; }

        public string ContactEmail { get; set; }
    }
}
