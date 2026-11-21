/// <summary>
/// AuditAttributionServiceTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Every service call site that writes an audit event records the caller's origin: MCP callers get
    /// channel "mcp" and their client label, REST callers leave both empty.
    /// </summary>
    [TestFixture]
    public class AuditAttributionServiceTests : ServiceTestBase
    {
        private const string Label = "copilot-cli/1.0.114";

        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly SecretAuditEventType[] AllServiceEvents =
        {
            SecretAuditEventType.SecretCreated,
            SecretAuditEventType.SecretMetadataUpdated,
            SecretAuditEventType.PermissionGranted,
            SecretAuditEventType.ContentWritten,
            SecretAuditEventType.ContentCommitted,
            SecretAuditEventType.ContentRead,
            SecretAuditEventType.PermissionRevoked,
            SecretAuditEventType.SecretDeleted,
        };

        [Test]
        public async Task McpCaller_EveryEventCarriesChannelAndLabel()
        {
            var mcpOwner = new Caller(SubjectType.User, OwnerUpn, OwnerUpn, Array.Empty<string>(), CallerChannels.Mcp, Label);

            var events = await this.RunAuditedLifecycleAsync(mcpOwner);

            Assert.That(events.Select(e => e.EventType), Is.EqualTo(AllServiceEvents));
            Assert.That(events.Select(e => e.Channel), Is.All.EqualTo("mcp"));
            Assert.That(events.Select(e => e.ClientLabel), Is.All.EqualTo(Label));
        }

        [Test]
        public async Task RestCaller_EveryEventHasAnEmptyOrigin()
        {
            var restOwner = new Caller(SubjectType.User, OwnerUpn, OwnerUpn, Array.Empty<string>(), CallerChannels.Rest, "ignored/1.0");

            var events = await this.RunAuditedLifecycleAsync(restOwner);

            Assert.That(events.Select(e => e.EventType), Is.EqualTo(AllServiceEvents));
            Assert.That(events.Select(e => e.Channel), Is.All.Empty);
            Assert.That(events.Select(e => e.ClientLabel), Is.All.Empty);
        }

        // One audited secret through every service method that appends an event, all as the given caller.
        private async Task<List<SecretAuditEvent>> RunAuditedLifecycleAsync(Caller caller)
        {
            var secrets = this.CreateSecretService();
            var contents = new ContentService(this.Configuration, this.DbContext, this.Purger, this.PermissionsManager, this.AuditWriter, NullLogger<ContentService>.Instance);
            var chunks = new ChunkService(this.Configuration, this.DbContext, this.Purger, this.BlobHelper, this.PermissionsManager, this.AuditWriter, NullLogger<ChunkService>.Instance);
            var access = this.CreateAccessService();

            AssertOk((await secrets.CreateAsync(caller, "alpha", CreationInput(auditEnabled: true))).Outcome);
            var auditInstanceId = (await this.DbContext.Objects.AsNoTracking().SingleAsync(o => o.ObjectName == "alpha")).AuditInstanceId;
            AssertOk((await secrets.UpdateAsync(caller, "alpha", new MetadataUpdateInput { ExpirationSettings = CreationInput().ExpirationSettings, Tags = new() { "x" } })).Outcome);
            AssertOk(await access.GrantAsync(caller, "alpha", ReaderCanRead()));

            var content = await contents.CreateAsync(caller, "alpha", string.Empty, new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf", IsImage = false });
            AssertOk(content.Outcome);
            var contentId = content.Value!.ContentName;
            var upload = await chunks.UploadChunkAsync(caller, "alpha", contentId, new MemoryStream(Hello), new ChunkUploadRequest
            {
                AccessTicket = string.Empty,
                ChunkHash = Sha256Hex(Hello),
                DeclaredLength = Hello.Length,
                ExpectedIndex = 0,
            });
            AssertOk(upload.Outcome);
            AssertOk((await contents.CommitAsync(caller, "alpha", contentId, upload.Value!.AccessTicket!, new ContentCommitInput(Sha256Hex(Hello)))).Outcome);
            var download = await chunks.DownloadChunkAsync(caller, "alpha", contentId, upload.Value!.ChunkName);
            AssertOk(download.Outcome);
            await download.Value!.WriteToAsync(new MemoryStream());

            AssertOk(await access.RevokeAsync(caller, "alpha", ReaderCanRead()));
            AssertOk(await secrets.DeleteAsync(caller, "alpha"));

            return await this.DbContext.SecretAuditEvents.AsNoTracking()
                .Where(e => e.AuditInstanceId == auditInstanceId)
                .OrderBy(e => e.SequenceNumber)
                .ToListAsync();
        }

        private AccessService CreateAccessService()
        {
            var features = new Features();
            var orphanedSecretConfiguration = new OrphanedSecretConfiguration();
            var orphanedSecretManager = new OrphanedSecretManager(
                Mock.Of<IOptionsMonitor<Features>>(x => x.CurrentValue == features),
                Mock.Of<IOptionsMonitor<OrphanedSecretConfiguration>>(x => x.CurrentValue == orphanedSecretConfiguration),
                NullLogger<OrphanedSecretManager>.Instance);
            return new AccessService(
                this.DbContext, new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance), this.PermissionsManager,
                this.AuditWriter, orphanedSecretManager, NullLogger<AccessService>.Instance);
        }

        private static List<SubjectPermissionsInput> ReaderCanRead()
            => new() { new SubjectPermissionsInput { SubjectType = SubjectTypeInput.User, SubjectName = ReaderUpn, CanRead = true } };

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private static void AssertOk(ServiceResult outcome)
            => Assert.That(outcome.IsOk, Is.True, $"Arrange: {outcome.Status} {outcome.Error}");
    }
}
