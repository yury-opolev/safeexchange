/// <summary>
/// ...
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;

    public class ExpirationMetadata
    {
        public ExpirationMetadata() { }

        public ExpirationMetadata(ExpirationSettingsInput expirationSettings)
        {
            this.ExpireAfterRead = expirationSettings.ExpireAfterRead;
            this.ScheduleExpiration = expirationSettings.ScheduleExpiration;
            this.ExpireAt = expirationSettings.ExpireAt;
        }

        public bool ExpireAfterRead { get; set; }

        public bool ScheduleExpiration { get; set; }

        public DateTime ExpireAt { get; set; }

        internal ExpirationSettingsOutput ToDto() => new ExpirationSettingsOutput()
        {
            ExpireAfterRead = this.ExpireAfterRead,
            ScheduleExpiration = this.ScheduleExpiration,
            ExpireAt = this.ExpireAt
        };
    }
}
