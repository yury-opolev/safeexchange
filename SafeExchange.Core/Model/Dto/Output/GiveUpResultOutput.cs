/// <summary>
/// GiveUpResultOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class GiveUpResultOutput
    {
        public bool HadDirectRow { get; set; }

        public bool WasOrphaned { get; set; }

        public DateTime? ExpireAt { get; set; }
    }
}
