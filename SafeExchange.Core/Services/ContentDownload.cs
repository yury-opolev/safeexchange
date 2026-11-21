/// <summary>
/// ContentDownload
/// </summary>

namespace SafeExchange.Core.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A successful download: the headers a host needs before the bytes, and the deferred write of the
    /// bytes. The data is read from storage, and the read is audited, only inside <see cref="WriteToAsync"/>.
    /// </summary>
    public sealed class ContentDownload
    {
        private readonly Func<Stream, CancellationToken, Task> writeTo;

        public ContentDownload(string contentType, long contentLength, string? contentDisposition, Func<Stream, CancellationToken, Task> writeTo)
        {
            this.ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
            this.ContentLength = contentLength;
            this.ContentDisposition = contentDisposition;
            this.writeTo = writeTo ?? throw new ArgumentNullException(nameof(writeTo));
        }

        public string ContentType { get; }

        public long ContentLength { get; }

        /// <summary>
        /// The <c>Content-Disposition</c> header value, or <c>null</c> when the download has none (chunks).
        /// </summary>
        public string? ContentDisposition { get; }

        public Task WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
            => this.writeTo(destination ?? throw new ArgumentNullException(nameof(destination)), cancellationToken);
    }
}
