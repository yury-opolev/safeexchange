/// <summary>
/// ChunkHashMismatch
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    public class ChunkHashMismatch
    {
        public string Expected { get; set; } = string.Empty;

        public string Actual { get; set; } = string.Empty;
    }
}
