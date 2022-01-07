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
            this.ScheduleExpiration = expirationSettings.ScheduleExpiration;
            this.ExpireAt = expirationSettings.ExpireAt;
            this.ExpireOnIdleTime = expirationSettings.ExpireOnIdleTime;
            this.IdleTimeToExpire = expirationSettings.IdleTimeToExpire;
        }

        public bool ScheduleExpiration { get; set; }

        public DateTime ExpireAt { get; set; }

        public bool ExpireOnIdleTime { get; set; }

        public TimeSpan IdleTimeToExpire { get; set; }

        internal ExpirationSettingsOutput ToDto() => new ExpirationSettingsOutput()
        {
            ScheduleExpiration = this.ScheduleExpiration,
            ExpireAt = this.ExpireAt,
            ExpireOnIdleTime = this.ExpireOnIdleTime,
            IdleTimeToExpire = this.IdleTimeToExpire
        };
    }
}
