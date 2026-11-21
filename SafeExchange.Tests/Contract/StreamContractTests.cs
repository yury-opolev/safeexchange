/// <summary>
/// StreamContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.EntityFrameworkCore;
    using NUnit.Framework;
    using SafeExchange.Core;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretStream: Run (chunk upload — legacy and
    /// hashed — and chunk download) and RunContentDownload (/all). Route-version agnostic.
    /// </summary>
    [TestFixture]
    public class StreamContractTests : ContractTestBase
    {
        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        [Test]
        public async Task LegacyUpload_Interim_ReturnsAccessTicket()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"","length":5,"accessTicket":"<accessTicket>"}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task LegacyUpload_FinalChunk_ReturnsEmptyAccessTicket()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));

            var response = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket);

            // Quirk: the final legacy chunk returns accessTicket "" (cleared), not null/omitted.
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000001","hash":"","length":5,"accessTicket":""}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task LegacyUpload_MainContent_IsSanitised_AndDownloadsAsHtml()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");

            var upload = await this.UploadChunkAsync("alpha", main, Encoding.UTF8.GetBytes("<p>hi</p><script>alert(1)</script>"));

            ContractAssert.Json(upload, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<main>-00000000","hash":"","length":9,"accessTicket":""}}
                """, Literals((main, "<main>")), "accessTicket");

            var download = await this.SecretStream.Run(Request("get"), "alpha", main, $"{main}-00000000", this.Owner, this.Logger);

            ContractAssert.Bytes(download, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<p>hi</p>"),
                "Content-Type: text/html; charset=utf-8",
                "Content-Length: 9");
        }

        [Test]
        public async Task HashedUpload_ReturnsServerHashAndAccessTicket()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello));

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824","length":5,"accessTicket":"<accessTicket>"}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task HashedUpload_ChunkHashMismatch_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(World));

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"chunk_hash_mismatch","subStatus":"",
                 "result":{"expected":"486ea46224d1bb4fb680f34f7c9ad96a8f24ec88be73ea8e5a6c65260e9cb8a7","actual":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"},
                 "error":"Received chunk bytes do not match the client-asserted hash."}
                """);
        }

        [Test]
        public async Task Upload_WithoutTicketWhileLocked_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            var response = await this.UploadChunkAsync("alpha", attachment, World);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }

        [Test]
        public async Task Upload_HashedAfterLegacyChunk_InconsistentUploadMode()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));

            var response = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket, interim: true, chunkHash: Sha256Hex(World));

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"inconsistent_upload_mode","subStatus":"","error":"Chunk-hash header usage is inconsistent with the content's upload mode."}
                """);
        }

        [Test]
        public async Task Upload_WithChunkId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.Run(BytesRequest("post", Hello), "alpha", attachment, "00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Cannot specify chunk id on upload."}
                """);
        }

        [Test]
        public async Task Upload_UnknownContent_NotFound()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", "missing-content", Hello);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Content 'missing-content' does not exist."}
                """);
        }

        [Test]
        public async Task ChunkDownload_ReturnsBytesWithContentHeaders()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Bytes(response, HttpStatusCode.OK, Hello,
                "Content-Type: application/pdf",
                "Content-Length: 5");
        }

        [Test]
        public async Task ChunkDownload_ContentNotReady_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is not ready."}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task ChunkDownload_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is being updated."}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task ChunkDownload_WithoutChunkId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Must specify chunk id on download."}
                """);
        }

        [Test]
        public async Task ChunkDownload_UnknownChunk_NotFound()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, "missing-chunk", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Chunk 'missing-chunk' does not exist."}
                """);
        }

        [Test]
        public async Task ChunkDownload_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task ContentDownload_ReturnsAllChunksWithDisposition()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));
            await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket);

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Bytes(response, HttpStatusCode.OK, Encoding.UTF8.GetBytes("helloworld"),
                "Content-Type: application/pdf",
                "Content-Length: 10",
                "Content-Disposition: attachment; filename=report.pdf");
        }

        [Test]
        public async Task ContentDownload_UnknownContent_NotFoundWithNotExistsWording()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", "missing-content", this.Owner, this.Logger);

            // Quirk: /all says "not exists." while the chunk routes say "does not exist."
            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Content 'missing-content' not exists."}
                """);
        }

        [TestCase("my report.pdf", "attachment; filename=\"my report.pdf\"")]
        [TestCase("отчёт.pdf", "attachment; filename=\"=?utf-8?B?0L7RgtGH0ZHRgi5wZGY=?=\"")]
        public async Task ContentDownload_FileNameIsEncodedInDisposition(string fileName, string expectedDisposition)
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha", fileName: fileName);
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Bytes(response, HttpStatusCode.OK, Hello,
                "Content-Type: application/pdf",
                "Content-Length: 5",
                $"Content-Disposition: {expectedDisposition}");
        }

        [Test]
        public async Task Upload_MissingSecret_NotFound()
        {
            var response = await this.UploadChunkAsync("missing", "any-content", Hello);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }

        [Test]
        public async Task Upload_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot use this endpoint for previous versions data."}
                """);
        }

        [Test]
        public async Task Upload_WithoutWritePermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, caller: this.Reader);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Write' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Upload_LegacyAfterHashedChunk_InconsistentUploadMode()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello)));

            var response = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket, interim: true);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"inconsistent_upload_mode","subStatus":"","error":"Chunk-hash header usage is inconsistent with the content's upload mode."}
                """);
        }

        [Test]
        public async Task ChunkDownload_MissingSecret_NotFound()
        {
            var response = await this.SecretStream.Run(Request("get"), "missing", "any-content", "any-chunk", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }

        [Test]
        public async Task ChunkDownload_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot use this endpoint for previous versions data."}
                """);
        }

        [Test]
        public async Task ContentDownload_MissingSecret_NotFound()
        {
            var response = await this.SecretStream.RunContentDownload(Request("get"), "missing", "any-content", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }

        [Test]
        public async Task ContentDownload_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot use this endpoint for previous versions data."}
                """);
        }

        [Test]
        public async Task ContentDownload_WithoutReadPermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello);

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Stranger, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Read' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task ContentDownload_ContentNotReady_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is not ready."}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task ContentDownload_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);

            var response = await this.SecretStream.RunContentDownload(Request("get"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is being updated."}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Run_UnknownMethod_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.Run(Request("delete"), "alpha", attachment, string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Request method not recognized"}
                """);
        }

        [Test]
        public async Task RunContentDownload_UnknownMethod_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.SecretStream.RunContentDownload(Request("post"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"error","subStatus":"","error":"Request method not recognized"}
                """);
        }

        [Test]
        public async Task Upload_WithExpiredTicket_ErasesStateAndRestartsNumbering()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var oldTicket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));
            DateTimeProvider.SpecifiedDateTime = FixedNow.AddMinutes(11);

            var response = await this.UploadChunkAsync("alpha", attachment, World, interim: true);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"","length":5,"accessTicket":"<accessTicket>"}}
                """, Literals((attachment, "<att>")), "accessTicket");
            Assert.That(TicketOf(response), Is.Not.EqualTo(oldTicket));
        }

        [Test]
        public async Task Upload_AtExactTicketTimeout_StillLocked()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);
            DateTimeProvider.SpecifiedDateTime = FixedNow.AddMinutes(10);

            var response = await this.UploadChunkAsync("alpha", attachment, World, interim: true);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }

        [Test]
        public async Task Upload_WithOldTicketAfterExpiry_RestartsNumberingWithoutRearmingLock()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var oldTicket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));
            DateTimeProvider.SpecifiedDateTime = FixedNow.AddMinutes(11);

            var response = await this.UploadChunkAsync("alpha", attachment, World, ticket: oldTicket, interim: true);

            // Quirk: the client still sends the expired ticket, so no new ticket is issued and the content stays unlocked.
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"","length":5,"accessTicket":"<ticket>"}}
                """, Literals((attachment, "<att>"), (oldTicket, "<ticket>")));
            var content = (await this.DbContext.Objects.FirstAsync(o => o.ObjectName == "alpha")).Content.Single(c => !c.IsMain);
            Assert.That(content.AccessTicket, Is.Empty);
        }

        [Test]
        public async Task ChunkDownload_AfterTicketExpiry_ReportsNotReadyAndDropsChunks()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Hello, interim: true);
            DateTimeProvider.SpecifiedDateTime = FixedNow.AddMinutes(11);

            var response = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content '<att>' is not ready."}
                """, Literals((attachment, "<att>")));
            var content = (await this.DbContext.Objects.FirstAsync(o => o.ObjectName == "alpha")).Content.Single(c => !c.IsMain);
            Assert.That(content.Chunks, Is.Empty);
        }

        [Test]
        public async Task LegacyUpload_WhenLegacyUploadsDisabled_RejectedButLockStaysSet()
        {
            this.BuildHandlers(("Features:AllowLegacyAttachmentUploads", "False"));
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"inconsistent_upload_mode","subStatus":"","error":"Chunk-hash header usage is inconsistent with the content's upload mode."}
                """);

            // Quirk: the access ticket is issued before the upload mode is resolved, so the rejected attempt locks the content.
            var retry = await this.UploadChunkAsync("alpha", attachment, Hello);
            ContractAssert.Json(retry, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }

        [Test]
        public async Task HashedUpload_WhenLegacyUploadsDisabled_Accepted()
        {
            this.BuildHandlers(("Features:AllowLegacyAttachmentUploads", "False"));
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello));

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824","length":5,"accessTicket":"<accessTicket>"}}
                """, Literals((attachment, "<att>")), "accessTicket");
        }

        [Test]
        public async Task HashedUpload_WhenChunkHashHeaderIgnored_ProcessedAsLegacy()
        {
            this.BuildHandlers(("Features:IgnoreChunkHashHeader", "True"));
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(World));

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<att>-00000000","hash":"","length":5,"accessTicket":""}}
                """, Literals((attachment, "<att>")));
            var download = await this.SecretStream.Run(Request("get"), "alpha", attachment, $"{attachment}-00000000", this.Owner, this.Logger);
            ContractAssert.Bytes(download, HttpStatusCode.OK, Hello, "Content-Type: application/pdf", "Content-Length: 5");
        }

        [Test]
        public async Task HashedUpload_WhenChunkHashHeaderIgnoredAndLegacyDisabled_Rejected()
        {
            this.BuildHandlers(("Features:IgnoreChunkHashHeader", "True"), ("Features:AllowLegacyAttachmentUploads", "False"));
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello));

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"inconsistent_upload_mode","subStatus":"","error":"Chunk-hash header usage is inconsistent with the content's upload mode."}
                """);
        }

        [Test]
        public async Task MainContentUpload_WithChunkHashHeader_IgnoresHeaderAndSanitises()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");
            var body = Encoding.UTF8.GetBytes("<p>hi</p><script>alert(1)</script>");

            var upload = await this.UploadChunkAsync("alpha", main, body, chunkHash: Sha256Hex(body));

            ContractAssert.Json(upload, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"chunkName":"<main>-00000000","hash":"","length":9,"accessTicket":""}}
                """, Literals((main, "<main>")));
            var download = await this.SecretStream.Run(Request("get"), "alpha", main, $"{main}-00000000", this.Owner, this.Logger);
            ContractAssert.Bytes(download, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<p>hi</p>"),
                "Content-Type: text/html; charset=utf-8",
                "Content-Length: 9");
        }
    }
}
