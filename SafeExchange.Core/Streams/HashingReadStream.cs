namespace SafeExchange.Core.Streams
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;
    using SafeExchange.Core.Crypto;

    /// <summary>
    /// Read-only stream wrapper that tees every byte it yields through two hashers:
    /// a per-chunk IncrementalHash and a cross-request SerializableSha256 running state.
    /// Used in the chunk-upload path so blob upload and hashing happen in one pass
    /// without buffering the chunk body.
    /// </summary>
    public sealed class HashingReadStream : Stream
    {
        private readonly Stream inner;
        private readonly IncrementalHash chunkHasher;
        private readonly SerializableSha256 running;

        public HashingReadStream(Stream inner, IncrementalHash chunkHasher, SerializableSha256 running)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.chunkHasher = chunkHasher ?? throw new ArgumentNullException(nameof(chunkHasher));
            this.running = running ?? throw new ArgumentNullException(nameof(running));
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = this.inner.Read(buffer, offset, count);
            if (read > 0)
            {
                this.chunkHasher.AppendData(buffer, offset, read);
                this.running.Append(new ReadOnlySpan<byte>(buffer, offset, read));
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await this.inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                var span = buffer.Span.Slice(0, read);
                this.chunkHasher.AppendData(span);
                this.running.Append(span);
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
