namespace SafeExchange.Tests.Tests
{
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using NUnit.Framework;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Streams;

    [TestFixture]
    public class HashingReadStreamTests
    {
        [Test]
        public async Task Read_ForwardsBytesAndHashesBoth()
        {
            var payload = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
            using var source = new MemoryStream(payload);
            var chunkHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var running = new SerializableSha256();
            await using var tee = new HashingReadStream(source, chunkHasher, running);

            using var sink = new MemoryStream();
            await tee.CopyToAsync(sink);

            Assert.That(sink.ToArray(), Is.EqualTo(payload));
            Assert.That(chunkHasher.GetHashAndReset(), Is.EqualTo(SHA256.HashData(payload)));
            Assert.That(running.Finish(), Is.EqualTo(SHA256.HashData(payload)));
        }

        [Test]
        public async Task Read_ChunksOfRandomSize_StillHashesCorrectly()
        {
            var rng = new System.Random(42);
            var payload = new byte[10_000];
            rng.NextBytes(payload);
            using var source = new MemoryStream(payload);
            var chunkHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var running = new SerializableSha256();
            await using var tee = new HashingReadStream(source, chunkHasher, running);

            using var sink = new MemoryStream();
            var buffer = new byte[17]; // odd size
            int read;
            while ((read = await tee.ReadAsync(buffer)) > 0)
            {
                sink.Write(buffer, 0, read);
            }

            Assert.That(sink.ToArray(), Is.EqualTo(payload));
            Assert.That(chunkHasher.GetHashAndReset(), Is.EqualTo(SHA256.HashData(payload)));
            Assert.That(running.Finish(), Is.EqualTo(SHA256.HashData(payload)));
        }
    }
}
