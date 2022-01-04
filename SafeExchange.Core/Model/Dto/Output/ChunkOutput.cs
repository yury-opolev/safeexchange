/// <summary>
/// ChunkOutput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class ChunkOutput
    {
        public string ChunkName { get; set; }

        public string Hash { get; set; }

        public long Length { get; set; }
    }
}
