/// <summary>
/// TestBlobHelper
/// </summary>

namespace SafeExchange.Tests
{
    using Azure;
    using SafeExchange.Core;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    public class TestBlobHelper : IBlobHelper
    {
        public readonly Dictionary<string, byte[]> Blobs = new();

        public async Task<bool> BlobExistsAsync(string blobName)
        {
            return await Task.FromResult(this.Blobs.ContainsKey(blobName));
        }

        public async Task<bool> DeleteBlobIfExistsAsync(string blobName)
        {
            if (!this.Blobs.ContainsKey(blobName))
            {
                return false;
            }

            this.Blobs.Remove(blobName);
            return await Task.FromResult(true);
        }

        public async Task<Stream> DownloadAndDecryptBlobAsync(string blobName)
        {
            if (!this.Blobs.ContainsKey(blobName))
            {
                throw new RequestFailedException("Blob not exists.");
            }

            var bytes = this.Blobs[blobName] ?? Array.Empty<byte>();
            return await Task.FromResult(new MemoryStream(bytes));
        }

        public async Task EncryptAndUploadBlobAsync(string blobName, Stream data)
        {
            var dataBytes = new byte[data.Length];
            await data.ReadAsync(dataBytes, 0, (int)data.Length);
            this.Blobs[blobName] = dataBytes;
        }
    }
}
