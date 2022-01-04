/// <summary>
/// ChunkMetadata
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Output;
    using System.ComponentModel.DataAnnotations;

    public class ChunkMetadata
    {
        [Key]
        public string ChunkName { get; set; }

        public string Hash { get; set; }

        public long Length { get; set; }

        internal ChunkOutput ToDto() => new()
        {
            ChunkName = this.ChunkName,
            Hash = this.Hash,
            Length = this.Length
        };

        internal ChunkCreationOutput ToCreationDto(string accessTicket) => new()
        {
            ChunkName = this.ChunkName,
            Hash = this.Hash,
            Length = this.Length,

            AccessTicket = accessTicket
        };
    }
}
