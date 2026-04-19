namespace SafeExchange.Tests.Tests
{
    using NUnit.Framework;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Stream;

    [TestFixture]
    public class UploadModeResolverTests
    {
        [Test]
        public void Resolve_HeaderPresent_NoPriorChunks_ReturnsHashed()
        {
            var content = NewContent();
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: true, allowLegacy: true, ignoreHeader: false);
            Assert.That(mode, Is.EqualTo(UploadMode.Hashed));
        }

        [Test]
        public void Resolve_HeaderPresent_HashedModeLocked_ReturnsHashed()
        {
            var content = NewContent();
            content.RunningHashState = new byte[] { 1 };
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: true, allowLegacy: true, ignoreHeader: false);
            Assert.That(mode, Is.EqualTo(UploadMode.Hashed));
        }

        [Test]
        public void Resolve_HeaderPresent_LegacyModeLocked_ReturnsReject()
        {
            var content = NewContent();
            content.Chunks.Add(new ChunkMetadata { ChunkName = "x", Hash = "", Length = 10 });
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: true, allowLegacy: true, ignoreHeader: false);
            Assert.That(mode, Is.EqualTo(UploadMode.Reject));
        }

        [Test]
        public void Resolve_HeaderAbsent_FlagOn_NoPriorChunks_ReturnsLegacy()
        {
            var content = NewContent();
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: false, allowLegacy: true, ignoreHeader: false);
            Assert.That(mode, Is.EqualTo(UploadMode.Legacy));
        }

        [Test]
        public void Resolve_HeaderAbsent_FlagOff_ReturnsReject()
        {
            var content = NewContent();
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: false, allowLegacy: false, ignoreHeader: false);
            Assert.That(mode, Is.EqualTo(UploadMode.Reject));
        }

        [Test]
        public void Resolve_HeaderAbsent_HashedModeLocked_ReturnsReject()
        {
            var content = NewContent();
            content.RunningHashState = new byte[] { 1 };
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: false, allowLegacy: true, ignoreHeader: false);
            Assert.That(mode, Is.EqualTo(UploadMode.Reject));
        }

        [Test]
        public void Resolve_IgnoreHeaderFlag_ForcesLegacy()
        {
            var content = NewContent();
            var mode = UploadModeResolver.Resolve(content, hashHeaderPresent: true, allowLegacy: true, ignoreHeader: true);
            Assert.That(mode, Is.EqualTo(UploadMode.Legacy));
        }

        private static ContentMetadata NewContent() => new ContentMetadata { ContentName = "c-00000000" };
    }
}
