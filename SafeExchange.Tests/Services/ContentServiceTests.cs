/// <summary>
/// ContentServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core.Crypto;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Linq;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    [TestFixture]
    public class ContentServiceTests : ServiceTestBase
    {
        private const string Ticket = "ticket-1";

        private ContentService service = null!;

        [SetUp]
        public void SetUp()
        {
            this.service = new ContentService(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<ContentService>.Instance);
        }

        [Test]
        public async Task Create_Owner_AddsABlankContentItem()
        {
            await this.CreateSecretAsync("alpha");

            var result = await this.service.CreateAsync(this.Owner, "alpha", string.Empty, Attachment());

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value!.ContentType, Is.EqualTo("application/pdf"));
            Assert.That(result.Value!.FileName, Is.EqualTo("report.pdf"));
            Assert.That(result.Value!.IsMain, Is.False);
        }

        [Test]
        public async Task EveryMethod_DeniesAReaderWithoutWrite()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            AssertInsufficientPermissions((await this.service.CreateAsync(this.Reader, "alpha", string.Empty, Attachment())).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.UpdateAsync(this.Reader, "alpha", contentId, new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" })).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.DropAsync(this.Reader, "alpha", contentId)).Outcome, PermissionType.Write, "alpha");
            AssertInsufficientPermissions(await this.service.DeleteAsync(this.Reader, "alpha", contentId), PermissionType.Write, "alpha");
            AssertInsufficientPermissions((await this.service.CommitAsync(this.Reader, "alpha", contentId, Ticket, new ContentCommitInput(new string('0', 64)))).Outcome, PermissionType.Write, "alpha");
        }

        [Test]
        public async Task Delete_MainContent_IsBadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var main = (await this.DbContext.Objects.SingleAsync(o => o.ObjectName == "alpha")).Content.Single(c => c.IsMain).ContentName;

            var result = await this.service.DeleteAsync(this.Owner, "alpha", main);

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(result.Error, Is.EqualTo("Cannot delete main content."));
        }

        [Test]
        public async Task Drop_LockedContent_IsUnprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.LockAsync("alpha", contentId, FixedNow);

            var result = await this.service.DropAsync(this.Owner, "alpha", contentId);

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.Unprocessable));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            Assert.That(result.Error, Is.EqualTo("Content is being updated."));
        }

        [Test]
        public async Task Drop_ExpiredLock_IsReleasedAndTheContentIsDropped()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.LockAsync("alpha", contentId, FixedNow.AddMinutes(-11));

            var result = await this.service.DropAsync(this.Owner, "alpha", contentId);

            Assert.That(result.IsOk, Is.True);
            var content = await this.ContentAsync("alpha", contentId);
            Assert.That(content.AccessTicket, Is.Empty);
            Assert.That(content.Status, Is.EqualTo(ContentStatus.Blank));
            Assert.That(content.Chunks, Is.Empty);
        }

        [Test]
        public async Task Update_UnknownContent_FailsInsideTheOperation()
        {
            await this.CreateSecretAsync("alpha");

            var thrown = Assert.ThrowsAsync<ServiceOperationException>(() => this.service.UpdateAsync(
                this.Owner, "alpha", "missing", new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" }));

            Assert.That(thrown!.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public async Task Commit_MalformedInput_IsAnsweredAfterTheLockCheck()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            await this.StartHashedUploadAsync("alpha", contentId, Encoding.UTF8.GetBytes("hello"));

            var wrongTicket = await this.service.CommitAsync(this.Owner, "alpha", contentId, "other", ContentCommitInput.Malformed);
            var malformed = await this.service.CommitAsync(this.Owner, "alpha", contentId, Ticket, ContentCommitInput.Malformed);

            Assert.That(wrongTicket.Status, Is.EqualTo(ServiceStatus.Unauthorized));
            Assert.That(wrongTicket.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(wrongTicket.Error, Is.EqualTo("Access ticket missing or invalid."));
            Assert.That(malformed.Status, Is.EqualTo(ServiceStatus.BadRequest));
            Assert.That(malformed.Error, Is.EqualTo("Body is not valid JSON."));
        }

        [Test]
        public async Task Commit_HashMismatch_CarriesBothHashes()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var data = Encoding.UTF8.GetBytes("hello");
            await this.StartHashedUploadAsync("alpha", contentId, data);
            var wrong = new string('a', 64);

            var result = await this.service.CommitAsync(this.Owner, "alpha", contentId, Ticket, new ContentCommitInput(wrong));

            Assert.That(result.Status, Is.EqualTo(ServiceStatus.HashMismatch));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
            var detail = (ChunkHashMismatch)result.Detail!;
            Assert.That(detail.Expected, Is.EqualTo(wrong));
            Assert.That(detail.Actual, Is.EqualTo(Sha256Hex(data)));
        }

        [Test]
        public async Task Commit_MatchingHash_MarksTheContentReady()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.CreateAttachmentAsync("alpha");
            var data = Encoding.UTF8.GetBytes("hello");
            await this.StartHashedUploadAsync("alpha", contentId, data);

            var result = await this.service.CommitAsync(this.Owner, "alpha", contentId, Ticket, new ContentCommitInput(Sha256Hex(data).ToUpperInvariant()));

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value!.Hash, Is.EqualTo(Sha256Hex(data)));
            var content = await this.ContentAsync("alpha", contentId);
            Assert.That(content.Status, Is.EqualTo(ContentStatus.Ready));
            Assert.That(content.AccessTicket, Is.Empty);
        }

        private static ContentMetadataCreationInput Attachment()
            => new() { ContentType = "application/pdf", FileName = "report.pdf", IsImage = false };

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private async Task<string> CreateAttachmentAsync(string secretId)
        {
            var result = await this.service.CreateAsync(this.Owner, secretId, string.Empty, Attachment());
            Assert.That(result.IsOk, Is.True, $"Arrange: attachment creation failed: {result.Error}");
            return result.Value!.ContentName;
        }

        private async Task<ContentMetadata> ContentAsync(string secretId, string contentId)
            => (await this.DbContext.Objects.SingleAsync(o => o.ObjectName == secretId)).Content.Single(c => c.ContentName == contentId);

        private async Task LockAsync(string secretId, string contentId, DateTime setAt)
        {
            var content = await this.ContentAsync(secretId, contentId);
            content.Status = ContentStatus.Updating;
            content.AccessTicket = Ticket;
            content.AccessTicketSetAt = setAt;
            await this.DbContext.SaveChangesAsync();
        }

        private async Task StartHashedUploadAsync(string secretId, string contentId, byte[] data)
        {
            await this.LockAsync(secretId, contentId, FixedNow);
            var running = new SerializableSha256();
            running.Append(data);
            var content = await this.ContentAsync(secretId, contentId);
            content.RunningHashState = running.SaveState();
            await this.DbContext.SaveChangesAsync();
        }
    }
}
