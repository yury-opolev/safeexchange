/// <summary>
/// DevBlobHelper — LOCAL SPIKE ONLY.
///
/// In-memory IBlobHelper used when SAEX_DEV_MODE=true so content/attachment
/// blobs work locally with no Azure Storage and no Key Vault encryption.
/// Data lives only for the process lifetime. Never use in a deployed environment.
///
/// Part of the LocalDev harness — see LocalDev/README.md.
/// </summary>

namespace SafeExchange.Core.LocalDev
{
    using SafeExchange.Core.Blob;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Threading.Tasks;

    public class DevBlobHelper : IBlobHelper
    {
        private readonly ConcurrentDictionary<string, byte[]> blobs = new();

        public Task<bool> BlobExistsAsync(string blobName)
            => Task.FromResult(this.blobs.ContainsKey(blobName));

        public async Task EncryptAndUploadBlobAsync(string blobName, Stream data)
        {
            using var memory = new MemoryStream();
            await data.CopyToAsync(memory);
            this.blobs[blobName] = memory.ToArray();
        }

        public Task<Stream> DownloadAndDecryptBlobAsync(string blobName)
        {
            if (!this.blobs.TryGetValue(blobName, out var bytes))
            {
                throw new FileNotFoundException($"Dev blob '{blobName}' not found.");
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public Task<bool> DeleteBlobIfExistsAsync(string blobName)
            => Task.FromResult(this.blobs.TryRemove(blobName, out _));
    }
}
