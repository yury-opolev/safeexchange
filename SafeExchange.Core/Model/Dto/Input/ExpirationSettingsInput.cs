/// <summary>
/// ExpirationSettingsInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class ExpirationSettingsInput
    {
        public bool ScheduleExpiration { get; set; }

        public DateTime ExpireAt { get; set; }

        public bool ExpireOnIdleTime { get; set; }

        public TimeSpan IdleTimeToExpire { get; set; }
    }
}
