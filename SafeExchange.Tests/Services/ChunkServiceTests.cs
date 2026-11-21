/// <summary>
/// ChunkServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    [TestFixture]
    public class ChunkServiceTests : ServiceTestBase
    {
        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        private ChunkService service = null!;

        private ContentService contentService = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = new ChunkService(this.Configuration, this.DbContext, this.Purger, this.BlobHelper, this.PermissionsManager, this.AuditWriter, NullLogger<ChunkService>.Instance);
            this.contentService = new ContentService(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<ContentService>.Instance);
        }

        [Test]
        public async Task EveryMethod_DeniesACallerWithoutThePermission()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            AssertInsufficientPermissions((await this.Upload(this.Reader, contentId, Hello)).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.DownloadChunkAsync(this.Stranger, "alpha", contentId, $"{contentId}-00000000")).Outcome, PermissionType.Read, "alpha");
            AssertInsufficientPermissions((await this.service.DownloadContentAsync(this.Stranger, "alpha", contentId)).Outcome, PermissionType.Read, "alpha");
            Assert.That(this.BlobHelper.Blobs, Is.Empty);
        }

        [Test]
        public async Task HashedUpload_Commit_AndDownload_RoundTrip()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");

            var first = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 0);
            var second = await this.Upload(this.Owner, contentId, World, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 1);
            var whole = Hello.Concat(World).ToArray();
            var commit = await this.contentService.CommitAsync(this.Owner, "alpha", contentId, first.Value!.AccessTicket!, new ContentCommitInput(Sha256Hex(whole)));
            var chunk = await this.service.DownloadChunkAsync(this.Owner, "alpha", contentId, second.Value!.ChunkName);
            var content = await this.service.DownloadContentAsync(this.Owner, "alpha", contentId);

            Assert.That(second.Value!.Hash, Is.EqualTo(Sha256Hex(World)));
            Assert.That(commit.IsOk, Is.True, commit.Error);
            Assert.That(chunk.Value!.ContentLength, Is.EqualTo(World.Length));
            Assert.That(chunk.Value!.ContentDisposition, Is.Null);
            Assert.That(await ReadAllAsync(chunk.Value!), Is.EqualTo(World));
            Assert.That(content.Value!.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(content.Value!.ContentLength, Is.EqualTo(whole.Length));
            Assert.That(content.Value!.ContentDisposition, Is.EqualTo("attachment; filename=report.pdf"));
            Assert.That(await ReadAllAsync(content.Value!), Is.EqualTo(whole));
        }

        [Test]
        public async Task Download_ReadsAndAuditsOnlyWhenWritten()
        {
            await this.CreateSecretAsync("alpha", auditEnabled: true);
            var contentId = await this.CreateAttachmentAsync("alpha");
            var upload = await this.Upload(this.Owner, contentId, Hello);
            Assert.That(upload.Value!.AccessTicket, Is.Empty, "Arrange: a final legacy chunk releases the lock.");

            var download = await this.service.DownloadChunkAsync(this.Owner, "alpha", contentId, upload.Value!.ChunkName);
            var readsBefore = await this.ContentReadEventsAsync();
            var bytes = await ReadAllAsync(download.Value!);
            var readsAfter = await this.ContentReadEventsAsync();

            Assert.That(bytes, Is.EqualTo(Hello));
            Assert.That(readsBefore, Is.EqualTo(0));
            Assert.That(readsAfter, Is.EqualTo(1));
        }

        [Test]
        public async Task Download_StorageFailureWhileWriting_IsAnOperationFailure()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var upload = await this.Upload(this.Owner, contentId, Hello);
            this.BlobHelper.Blobs.Clear();

            var download = await this.service.DownloadChunkAsync(this.Owner, "alpha", contentId, upload.Value!.ChunkName);

            Assert.That(download.IsOk, Is.True);
            Assert.ThrowsAsync<ServiceOperationException>(() => download.Value!.WriteToAsync(new MemoryStream()));
        }

        [Test]
        public async Task ExpectedIndex_RetryOfAStoredChunkWithTheSameHash_IsANoOp()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var first = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 0);

            var retry = await this.Upload(this.Owner, contentId, Hello, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 0);

            Assert.That(retry.IsOk, Is.True, retry.Error);
            Assert.That(retry.Value!.ChunkName, Is.EqualTo(first.Value!.ChunkName));
            Assert.That(retry.Value!.Hash, Is.EqualTo(Sha256Hex(Hello)));
            Assert.That(retry.Value!.AccessTicket, Is.EqualTo(first.Value!.AccessTicket));
            Assert.That((await this.ContentAsync(contentId)).Chunks, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task ExpectedIndex_NotTheNextIndex_IsUnprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var first = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 0);

            var skipped = await this.Upload(this.Owner, contentId, World, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 2);
            var changed = await this.Upload(this.Owner, contentId, World, hashed: true, ticket: first.Value!.AccessTicket, expectedIndex: 0);

            foreach (var result in new[] { skipped, changed })
            {
                Assert.That(result.Status, Is.EqualTo(ServiceStatus.Unprocessable));
                Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
                Assert.That(result.Error, Is.EqualTo("Expected chunk index 1."));
            }

            Assert.That((await this.ContentAsync(contentId)).Chunks, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task ExpectedIndex_Mismatch_DoesNotTakeTheLock()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");

            var result = await this.Upload(this.Owner, contentId, Hello, hashed: true, expectedIndex: 3);

            Assert.That(result.Error, Is.EqualTo("Expected chunk index 0."));
            var content = await this.ContentAsync(contentId);
            Assert.That(content.AccessTicket, Is.Empty);
            Assert.That(content.Status, Is.EqualTo(ContentStatus.Blank));
        }

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static async Task<byte[]> ReadAllAsync(ContentDownload download)
        {
            using var buffer = new MemoryStream();
            await download.WriteToAsync(buffer);
            return buffer.ToArray();
        }

        private Task<ServiceResult<ChunkCreationOutput>> Upload(
            Caller caller, string contentId, byte[] data, bool hashed = false, string? ticket = null, int? expectedIndex = null)
            => this.service.UploadChunkAsync(caller, "alpha", contentId, new MemoryStream(data), new ChunkUploadRequest
            {
                AccessTicket = ticket ?? string.Empty,
                ChunkHash = hashed ? Sha256Hex(data) : null,
                DeclaredLength = hashed ? data.Length : 0,
                ExpectedIndex = expectedIndex,
            });

        private async Task<string> CreateAttachmentAsync(string secretId)
        {
            var input = new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf", IsImage = false };
            var result = await this.contentService.CreateAsync(this.Owner, secretId, string.Empty, input);
            Assert.That(result.IsOk, Is.True, $"Arrange: attachment creation failed: {result.Error}");
            return result.Value!.ContentName;
        }

        private async Task<ContentMetadata> ContentAsync(string contentId)
            => (await this.DbContext.Objects.SingleAsync(o => o.ObjectName == "alpha")).Content.Single(c => c.ContentName == contentId);

        private Task<int> ContentReadEventsAsync()
            => this.DbContext.SecretAuditEvents.AsNoTracking().CountAsync(e => e.EventType == SecretAuditEventType.ContentRead);
    }
}
