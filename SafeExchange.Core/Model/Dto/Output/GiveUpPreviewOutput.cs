/// <summary>
/// GiveUpPreviewOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class GiveUpPreviewOutput
    {
        public bool HasDirectRow { get; set; }

        public bool WouldOrphan { get; set; }

        public DateTime? CurrentExpireAt { get; set; }

        public DateTime? ProspectiveExpireAt { get; set; }
    }
}
