/// <summary>
/// ContentMetadata
/// </summary>

namespace SafeExchange.Core.Model
{
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;

    public class ContentMetadata
    {
        public ContentMetadata()
        {
            this.Chunks = new List<ChunkMetadata>();
        }

        public ContentMetadata(ContentMetadataCreationInput input)
        {
            this.ContentName = ContentMetadata.NewName();

            this.IsMain = false;
            this.ContentType = input.ContentType;
            this.FileName = input.FileName;
            this.Status = ContentStatus.Blank;
            this.AccessTicket = string.Empty;
            this.AccessTicketSetAt = DateTime.MinValue;

            Chunks = new List<ChunkMetadata>();
        }

        public static string NewName()
        {
            var utcNow = DateTimeProvider.UtcNow;
            return $"BLOB-{utcNow:yyyyMMddHHmmss}-{Guid.NewGuid()}";
        }

        [Key]
        public string ContentName { get; set; }

        public bool IsMain { get; set; }

        public string ContentType { get; set; }

        public string FileName { get; set; }

        public ContentStatus Status { get; set; }

        public string AccessTicket { get; set; }

        public DateTime AccessTicketSetAt { get; set; }

        public List<ChunkMetadata> Chunks { get; set; }

        public void SetChunkProperties(int index, string hash, long length)
        {
            if (index < 0 || index >= this.Chunks.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var chunk = this.Chunks[index];
            chunk.Hash = hash;
            chunk.Length = length;
        }

        public List<ChunkMetadata> DeleteChunks()
        {
            var removedChunks = this.Chunks;
            this.Chunks.Clear();
            return removedChunks;
        }

        internal ContentMetadataOutput ToDto() => new ()
        {
            ContentName = this.ContentName,
            IsMain = this.IsMain,
            ContentType = this.ContentType,
            FileName = this.FileName,
            IsReady = this.Status == ContentStatus.Ready,
            Chunks = this.Chunks?.Select(x => x.ToDto()).ToList() ?? Array.Empty<ChunkOutput>().ToList()
        };
    }
}
