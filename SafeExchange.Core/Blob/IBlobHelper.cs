/// <summary>
/// IBlobHelper
/// </summary>

namespace SafeExchange.Core
{
    using System;

    /// <summary>
    /// Interface to provide azure storage blob operaions.
    /// </summary>
    public interface IBlobHelper
    {
        /// <summary>
        /// Check if a blob with given name exists in azure storage container. Asynchronous.
        /// </summary>
        /// <param name="blobName">Name of the blob.</param>
        /// <returns>True if blob with a given name exists in the container, false otherwise.</returns>
        public Task<bool> BlobExistsAsync(string blobName);

        /// <summary>
        /// Encrypt data from the stream and upload to the blob with given name. Asynchronous.
        /// </summary>
        /// <param name="blobName">Name of the blob.</param>
        /// <param name="data">Source stream with unencrypted data.</param>
        /// <returns>A task representing asynchronous action.</returns>
        public Task EncryptAndUploadBlobAsync(string blobName, Stream data);

        /// <summary>
        /// Download data from the blob with the given name and decrypt, return result as a stream. Asynchronous.
        /// </summary>
        /// <param name="blobName">Name of the blob.</param>
        /// <returns>A stream with unencrypted data to consume.</returns>
        public Task<Stream> DownloadAndDecryptBlobAsync(string blobName);

        /// <summary>
        /// Delete blob with given name from the storage container, if exists. Asynchronous action.
        /// </summary>
        /// <param name="blobName">Name of the blob.</param>
        /// <returns>True if blob existed and was deleted, false otherwise.</returns>
        public Task<bool> DeleteBlobIfExistsAsync(string blobName);
    }
}
