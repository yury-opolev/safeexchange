/// <summary>
/// ContentCommitContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core.Functions;
    using System.Net;
    using System.Security.Claims;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeContentCommit (PATCH .../commit). Route-version agnostic.
    /// </summary>
    [TestFixture]
    public class ContentCommitContractTests : ContractTestBase
    {
        private const string HelloWorldSha256 = "936a185caaa266bb9cbe981e9e05cb78cd732b0b3280eb944412bb6f8f8f07af";

        private static readonly byte[] Hello = Encoding.UTF8.GetBytes("hello");

        private static readonly byte[] World = Encoding.UTF8.GetBytes("world");

        [Test]
        public async Task Commit_MatchingHash_ReturnsContentHash()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","hash":"936a185caaa266bb9cbe981e9e05cb78cd732b0b3280eb944412bb6f8f8f07af"}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Commit_DifferentHash_HashMismatch()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{Sha256Hex(Hello)}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"hash_mismatch","subStatus":"",
                 "result":{"expected":"2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824","actual":"936a185caaa266bb9cbe981e9e05cb78cd732b0b3280eb944412bb6f8f8f07af"},
                 "error":"Client-asserted whole-content hash does not match server computation."}
                """);
        }

        [Test]
        public async Task Commit_WrongTicket_Unauthorized()
        {
            var (attachment, _) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, "not-the-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Access ticket missing or invalid."}
                """);
        }

        [Test]
        public async Task Commit_LegacyUploadInProgress_NoUploadState()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, interim: true));

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{Sha256Hex(Hello)}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"no_upload_state","subStatus":"","error":"No hashed-mode upload in progress for this content."}
                """);
        }

        [Test]
        public async Task Commit_HashNotHex_BadRequest()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, "{\"hash\":\"xyz\"}");

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"hash must be 64 hex characters."}
                """);
        }

        [Test]
        public async Task Commit_BodyNotJson_BadRequest()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();

            var response = await this.CommitAsync(attachment, ticket, "not json");

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Body is not valid JSON."}
                """);
        }

        [Test]
        public async Task Commit_MainContent_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");

            var response = await this.CommitAsync(main, "any-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Main content does not support explicit commit."}
                """);
        }

        [Test]
        public async Task Commit_PreviousVersionSecret_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.MarkAsPreviousVersionAsync("alpha");

            var response = await this.CommitAsync(attachment, "any-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Cannot commit data for previous-version secrets."}
                """);
        }

        [Test]
        public async Task Commit_UnknownContent_NotFound()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.CommitAsync("missing-content", "any-ticket", $"{{\"hash\":\"{HelloWorldSha256}\"}}");

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Content 'missing-content' does not exist."}
                """);
        }

        [Test]
        public async Task Commit_MissingSecret_NotFound()
        {
            var response = await this.ContentCommit.Run(
                TextRequest("patch", $"{{\"hash\":\"{HelloWorldSha256}\"}}"), "missing", "any-content", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.NotFound, """
                {"status":"not_found","subStatus":"","error":"Secret 'missing' does not exist."}
                """);
        }

        [Test]
        public async Task Commit_WithoutWritePermission_Forbidden()
        {
            var (attachment, ticket) = await this.UploadHashedHelloWorldAsync();
            await this.GrantAsync("alpha", ReaderUpn, read: true);

            var response = await this.CommitAsync(attachment, ticket, $"{{\"hash\":\"{HelloWorldSha256}\"}}", this.Reader);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Write' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Commit_MissingTicketHeader_Unauthorized()
        {
            var (attachment, _) = await this.UploadHashedHelloWorldAsync();

            var response = await this.ContentCommit.Run(
                TextRequest("patch", $"{{\"hash\":\"{HelloWorldSha256}\"}}"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Unauthorized, """
                {"status":"unauthorized","subStatus":"","error":"Access ticket missing or invalid."}
                """);
        }

        private async Task<(string Attachment, string Ticket)> UploadHashedHelloWorldAsync()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var ticket = TicketOf(await this.UploadChunkAsync("alpha", attachment, Hello, chunkHash: Sha256Hex(Hello)));
            var second = await this.UploadChunkAsync("alpha", attachment, World, ticket: ticket, chunkHash: Sha256Hex(World));
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK), "Arrange: second hashed chunk failed.");
            return (attachment, ticket);
        }

        private async Task<HttpResponseData> CommitAsync(string contentId, string ticket, string body, ClaimsPrincipal? caller = null)
        {
            var request = TextRequest("patch", body);
            request.Headers.Add(SafeExchangeSecretStream.AccessTicketHeaderName, ticket);
            return await this.ContentCommit.Run(request, "alpha", contentId, caller ?? this.Owner, this.Logger);
        }
    }
}
