/// <summary>
/// McpFileToolsTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.EntityFrameworkCore;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
    using System;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// In-band file transfer over the real services: file_upload_begin, file_upload_chunk (with resume),
    /// file_upload_complete, file_download_chunk and file_delete.
    /// </summary>
    [TestFixture]
    public class McpFileToolsTests : McpTestBase
    {
        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        [Test]
        public async Task InBandUpload_WithResume_ThenDownload_MatchesTheHashes()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.BeginAsync("alpha");

            var first = Ok(await this.ChunkAsync(contentId, 0, Hello));
            var ticket = first["ticket"]!.GetValue<string>();
            var retried = Ok(await this.ChunkAsync(contentId, 0, Hello, ticket));
            var skipped = await this.ChunkAsync(contentId, 2, World, ticket);
            var second = Ok(await this.ChunkAsync(contentId, 1, World, ticket));
            var whole = Hello.Concat(World).ToArray();
            var completed = Ok(await this.CallToolAsync(this.McpOwner, "file_upload_complete", new { name = "alpha", contentId, sha256 = Sha256Hex(whole), ticket }));

            Assert.That(first["sha256"]!.GetValue<string>(), Is.EqualTo(Sha256Hex(Hello)));
            Assert.That(first["nextIndex"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(retried["ticket"]!.GetValue<string>(), Is.EqualTo(ticket));
            AssertToolError(skipped, "unprocessable", "Expected chunk index 1.");
            Assert.That(second["length"]!.GetValue<long>(), Is.EqualTo(World.Length));
            Assert.That(completed["sha256"]!.GetValue<string>(), Is.EqualTo(Sha256Hex(whole)));

            var file = Ok(await this.CallToolAsync(this.McpOwner, "secret_get", new { name = "alpha" }))["content"]!.AsArray()
                .Single(c => c!["contentId"]!.GetValue<string>() == contentId)!;
            Assert.That(file["isReady"]!.GetValue<bool>(), Is.True);
            Assert.That(file["size"]!.GetValue<long>(), Is.EqualTo(whole.Length));
            Assert.That(file["chunkCount"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(file["sha256"]!.GetValue<string>(), Is.EqualTo(Sha256Hex(whole)));

            var downloaded = Array.Empty<byte>();
            for (var index = 0; index < 2; index++)
            {
                var chunk = Ok(await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId, index }));
                var bytes = Convert.FromBase64String(chunk["untrusted"]!["data"]!.GetValue<string>());
                Assert.That(chunk["sha256"]!.GetValue<string>(), Is.EqualTo(Sha256Hex(bytes)));
                Assert.That(chunk["chunkCount"]!.GetValue<int>(), Is.EqualTo(2));
                Assert.That(chunk["fileSha256"]!.GetValue<string>(), Is.EqualTo(Sha256Hex(whole)));
                downloaded = downloaded.Concat(bytes).ToArray();
            }

            Assert.That(downloaded, Is.EqualTo(whole));
            AssertToolError(
                await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId, index = 2 }),
                "not_found", "Chunk 2 does not exist; the file has 2 chunk(s).");
        }

        [Test]
        public async Task FileUploadBegin_KeepsNameAndTypeUntrusted_AndNeedsWrite()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);

            var image = Ok(await this.CallToolAsync(this.McpOwner, "file_upload_begin", new { name = "alpha", fileName = "photo.png", contentType = "image/png" }));
            var plain = Ok(await this.CallToolAsync(this.McpOwner, "file_upload_begin", new { name = "alpha", fileName = "notes.bin" }));
            var reader = await this.CallToolAsync(this.McpReader, "file_upload_begin", new { name = "alpha", fileName = "x.bin" });

            Assert.That(image["isImage"]!.GetValue<bool>(), Is.True);
            Assert.That(image["untrusted"]!.ToJsonString(), Is.EqualTo("""{"fileName":"photo.png","contentType":"image/png"}"""));
            Assert.That(plain["untrusted"]!["contentType"]!.GetValue<string>(), Is.EqualTo("application/octet-stream"));
            AssertToolError(reader, "forbidden", "Insufficient permissions to do 'Write' action on 'alpha'");
        }

        [Test]
        public async Task FileUploadChunk_ChecksTheChunkHashAndItsArguments()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.BeginAsync("alpha");

            var mismatch = await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index = 0, data = Convert.ToBase64String(Hello), sha256 = Sha256Hex(World) });
            var notBase64 = await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index = 0, data = "%%%", sha256 = Sha256Hex(Hello) });
            var badHash = await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index = 0, data = Convert.ToBase64String(Hello), sha256 = "abc" });
            var negative = await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index = -1, data = Convert.ToBase64String(Hello), sha256 = Sha256Hex(Hello) });

            AssertToolError(mismatch, "chunk_hash_mismatch", "Received chunk bytes do not match the client-asserted hash.");
            AssertToolError(notBase64, "invalid_arguments", "'data' must be base64.");
            AssertToolError(badHash, "invalid_arguments", "'sha256' must be 64 hex characters.");
            AssertToolError(negative, "invalid_arguments", "'index' must be a non-negative integer.");
        }

        [Test]
        public async Task FileUploadComplete_WrongWholeFileHash_IsAHashMismatch()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.BeginAsync("alpha");
            var ticket = Ok(await this.ChunkAsync(contentId, 0, Hello))["ticket"]!.GetValue<string>();

            var result = await this.CallToolAsync(this.McpOwner, "file_upload_complete", new { name = "alpha", contentId, sha256 = Sha256Hex(World), ticket });

            AssertToolError(result, "hash_mismatch", "Client-asserted whole-content hash does not match server computation.");
        }

        [Test]
        public async Task FileDownloadChunk_NeedsRead()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.UploadAsync("alpha", Hello);

            var stranger = await this.CallToolAsync(this.McpStranger, "file_download_chunk", new { name = "alpha", contentId, index = 0 });

            AssertToolError(stranger, "forbidden", "Insufficient permissions to do 'Read' action on 'alpha'");
        }

        [Test]
        public async Task FileDownloadChunk_OfTheMainContent_NeedsReveal_AndDoesNotReadBeforeIt()
        {
            Ok(await this.CallToolAsync(this.McpOwner, "secret_create", new { name = "alpha", expirationSettings = Expiration(), auditEnabled = true, text = "<p>top secret</p>" }));
            var mainId = Ok(await this.CallToolAsync(this.McpOwner, "secret_get", new { name = "alpha" }))["content"]!.AsArray()
                .Single(c => c!["isMain"]!.GetValue<bool>())!["contentId"]!.GetValue<string>();

            var unrevealed = await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId = mainId, index = 0 });
            var falseReveal = await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId = mainId, index = 0, reveal = false });
            var stranger = await this.CallToolAsync(this.McpStranger, "file_download_chunk", new { name = "alpha", contentId = mainId, index = 0, reveal = true });
            var readsBefore = await this.ContentReadEventsAsync();
            var revealed = Ok(await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId = mainId, index = 0, reveal = true }));

            var message = $"Content '{mainId}' is the secret's main text value; downloading it puts the value into this conversation and sends it to the model provider. Prefer secret_read_text, or call again with reveal=true.";
            AssertToolError(unrevealed, "reveal_required", message);
            AssertToolError(falseReveal, "reveal_required", message);
            AssertToolError(stranger, "forbidden", "Insufficient permissions to do 'Read' action on 'alpha'");
            Assert.That(readsBefore, Is.EqualTo(0));
            Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(revealed["untrusted"]!["data"]!.GetValue<string>())), Does.Contain("top secret"));
            Assert.That(await this.ContentReadEventsAsync(), Is.EqualTo(1));
        }

        [Test]
        public async Task FileDownloadChunk_OfAnAttachment_DoesNotNeedReveal()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.UploadAsync("alpha", Hello);

            var chunk = Ok(await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId, index = 0 }));

            Assert.That(Convert.FromBase64String(chunk["untrusted"]!["data"]!.GetValue<string>()), Is.EqualTo(Hello));
        }

        [Test]
        public async Task FileUploadChunk_RetryOfChunkZeroWithoutTheTicket_ReturnsTheStoredTicket()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.BeginAsync("alpha");

            var first = Ok(await this.ChunkAsync(contentId, 0, Hello));
            var ticket = first["ticket"]!.GetValue<string>();
            var retried = Ok(await this.ChunkAsync(contentId, 0, Hello));
            var second = Ok(await this.ChunkAsync(contentId, 1, World, retried["ticket"]!.GetValue<string>()));
            var whole = Hello.Concat(World).ToArray();
            var completed = Ok(await this.CallToolAsync(this.McpOwner, "file_upload_complete", new { name = "alpha", contentId, sha256 = Sha256Hex(whole), ticket }));

            Assert.That(retried["ticket"]!.GetValue<string>(), Is.EqualTo(ticket));
            Assert.That(retried["nextIndex"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(second["nextIndex"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(completed["sha256"]!.GetValue<string>(), Is.EqualTo(Sha256Hex(whole)));
        }

        [Test]
        public async Task FileUploadChunk_ChunkHashMismatch_LeavesTheFileFreeToRetryAndDelete()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.BeginAsync("alpha");

            var mismatch = await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index = 0, data = Convert.ToBase64String(Hello), sha256 = Sha256Hex(World) });
            var retried = Ok(await this.ChunkAsync(contentId, 0, Hello));
            var secondMismatch = await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index = 1, data = Convert.ToBase64String(World), sha256 = Sha256Hex(Hello), ticket = retried["ticket"]!.GetValue<string>() });
            var abandoned = await this.BeginAsync("alpha");
            await this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId = abandoned, index = 0, data = Convert.ToBase64String(Hello), sha256 = Sha256Hex(World) });
            var deleted = Ok(await this.CallToolAsync(this.McpOwner, "file_delete", new { name = "alpha", contentId = abandoned, confirm = true }));

            AssertToolError(mismatch, "chunk_hash_mismatch", "Received chunk bytes do not match the client-asserted hash.");
            Assert.That(retried["ticket"]!.GetValue<string>(), Is.Not.Empty);
            Assert.That(secondMismatch["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(deleted["deleted"]!.GetValue<bool>(), Is.True);
        }

        [Test]
        public async Task FileUploadChunk_ExtraChunkOnACommittedFile_IsRejectedAndTheFileStaysIntact()
        {
            await this.CreateSecretAsync("alpha");
            var contentId = await this.BeginAsync("alpha");
            var ticket = Ok(await this.ChunkAsync(contentId, 0, Hello))["ticket"]!.GetValue<string>();
            Ok(await this.CallToolAsync(this.McpOwner, "file_upload_complete", new { name = "alpha", contentId, sha256 = Sha256Hex(Hello), ticket }));

            var extra = await this.ChunkAsync(contentId, 1, World, ticket);
            DateTimeProvider.SpecifiedDateTime = FixedNow.AddMinutes(11);
            var chunk = Ok(await this.CallToolAsync(this.McpOwner, "file_download_chunk", new { name = "alpha", contentId, index = 0 }));

            Assert.That(extra["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(Convert.FromBase64String(chunk["untrusted"]!["data"]!.GetValue<string>()), Is.EqualTo(Hello));
            var file = Ok(await this.CallToolAsync(this.McpOwner, "secret_get", new { name = "alpha" }))["content"]!.AsArray()
                .Single(c => c!["contentId"]!.GetValue<string>() == contentId)!;
            Assert.That(file["isReady"]!.GetValue<bool>(), Is.True);
            Assert.That(file["chunkCount"]!.GetValue<int>(), Is.EqualTo(1));
        }

        [Test]
        public async Task FileDelete_NeedsConfirmAndWrite()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, PermissionType.Read);
            var contentId = await this.UploadAsync("alpha", Hello);

            var unconfirmed = await this.CallToolAsync(this.McpOwner, "file_delete", new { name = "alpha", contentId });
            var reader = await this.CallToolAsync(this.McpReader, "file_delete", new { name = "alpha", contentId, confirm = true });
            var countBefore = this.BlobHelper.Blobs.Count;
            var deleted = Ok(await this.CallToolAsync(this.McpOwner, "file_delete", new { name = "alpha", contentId, confirm = true }));

            AssertToolError(unconfirmed, "confirmation_required", $"Deleting file '{contentId}' of secret 'alpha' removes it for everyone. Call again with confirm=true to delete it.");
            AssertToolError(reader, "forbidden", "Insufficient permissions to do 'Write' action on 'alpha'");
            Assert.That(countBefore, Is.EqualTo(1));
            Assert.That(deleted["deleted"]!.GetValue<bool>(), Is.True);
            Assert.That(this.BlobHelper.Blobs, Is.Empty);
        }

        private Task<int> ContentReadEventsAsync()
            => this.DbContext.SecretAuditEvents.AsNoTracking().CountAsync(e => e.EventType == SecretAuditEventType.ContentRead);

        private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        private async Task<string> BeginAsync(string secretId)
        {
            var begun = Ok(await this.CallToolAsync(this.McpOwner, "file_upload_begin", new { name = secretId, fileName = "report.pdf", contentType = "application/pdf" }));
            return begun["contentId"]!.GetValue<string>();
        }

        private Task<JsonObject> ChunkAsync(string contentId, int index, byte[] data, string? ticket = null)
            => this.CallToolAsync(this.McpOwner, "file_upload_chunk", new { name = "alpha", contentId, index, data = Convert.ToBase64String(data), sha256 = Sha256Hex(data), ticket });

        private async Task<string> UploadAsync(string secretId, byte[] data)
        {
            var contentId = await this.BeginAsync(secretId);
            var ticket = Ok(await this.ChunkAsync(contentId, 0, data))["ticket"]!.GetValue<string>();
            Ok(await this.CallToolAsync(this.McpOwner, "file_upload_complete", new { name = secretId, contentId, sha256 = Sha256Hex(data), ticket }));
            return contentId;
        }
    }
}
