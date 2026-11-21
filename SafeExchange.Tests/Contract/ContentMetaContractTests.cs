/// <summary>
/// ContentMetaContractTests
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using NUnit.Framework;
    using SafeExchange.Core.Model.Dto.Input;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Pins the wire contract of SafeExchangeSecretContentMeta: Run (post/patch/delete) and
    /// RunDrop (patch). Route-version agnostic.
    /// </summary>
    [TestFixture]
    public class ContentMetaContractTests : ContractTestBase
    {
        [Test]
        public async Task Create_ReturnsNewContent()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataCreationInput { ContentType = "image/png", FileName = "pic.png", IsImage = true };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", string.Empty, this.Owner, this.Logger);

            var attachment = ReadJson(response)["result"]!["contentName"]!.GetValue<string>();
            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","isMain":false,"isImage":true,"contentType":"image/png","fileName":"pic.png","isReady":false,"chunks":[]}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Create_WithContentId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf" };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", "named", this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Cannot specify content name on creation."}
                """);
        }

        [Test]
        public async Task Create_WithoutWritePermission_Forbidden()
        {
            await this.CreateSecretAsync("alpha");
            await this.GrantAsync("alpha", ReaderUpn, read: true);
            var input = new ContentMetadataCreationInput { ContentType = "application/pdf", FileName = "report.pdf" };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", string.Empty, this.Reader, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.Forbidden, """
                {"status":"forbidden","subStatus":"","error":"Insufficient permissions to do 'Write' action on 'alpha'"}
                """);
        }

        [Test]
        public async Task Create_WithoutContentType_BadRequest()
        {
            await this.CreateSecretAsync("alpha");

            var response = await this.ContentMeta.Run(TextRequest("post", "{\"fileName\":\"report.pdf\"}"), "alpha", string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Content settings are not provided."}
                """);
        }

        [Test]
        public async Task Create_ContentTypeWithControlCharacters_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataCreationInput { ContentType = "text/plain\r\nX-Injected: 1", FileName = "a.txt" };

            var response = await this.ContentMeta.Run(JsonRequest("post", input), "alpha", string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Content type is in incorrect format."}
                """);
        }

        [Test]
        public async Task Update_ReturnsUpdatedContent_NullFileNameBecomesEmpty()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            var input = new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = null! };

            var response = await this.ContentMeta.Run(JsonRequest("patch", input), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","isMain":false,"isImage":false,"contentType":"text/plain","fileName":"","isReady":false,"chunks":[]}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Update_WithoutContentId_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" };

            var response = await this.ContentMeta.Run(JsonRequest("patch", input), "alpha", string.Empty, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Must specify content name on update."}
                """);
        }

        [Test]
        public async Task Update_UnknownContent_InternalError()
        {
            await this.CreateSecretAsync("alpha");
            var input = new ContentMetadataUpdateInput { ContentType = "text/plain", FileName = "a.txt" };

            var response = await this.ContentMeta.Run(JsonRequest("patch", input), "alpha", "missing-content", this.Owner, this.Logger);

            // Quirk: content lookups here use First(), so an unknown content id is a 500, not a 404.
            ContractAssert.Json(response, HttpStatusCode.InternalServerError, """
                {"status":"error","subStatus":"internal_exception","error":"Internal error. Reference: <correlationId>"}
                """);
        }

        [Test]
        public async Task Drop_ClearsChunksAndReturnsBlankContent()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Encoding.UTF8.GetBytes("hello"));

            var response = await this.ContentMeta.RunDrop(Request("patch"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":{"contentName":"<att>","isMain":false,"isImage":false,"contentType":"application/pdf","fileName":"report.pdf","isReady":false,"chunks":[]}}
                """, Literals((attachment, "<att>")));
        }

        [Test]
        public async Task Drop_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Encoding.UTF8.GetBytes("hello"), interim: true);

            var response = await this.ContentMeta.RunDrop(Request("patch"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }

        [Test]
        public async Task Delete_ReturnsOk()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");

            var response = await this.ContentMeta.Run(Request("delete"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Delete_MainContent_BadRequest()
        {
            await this.CreateSecretAsync("alpha");
            var main = await this.MainContentNameAsync("alpha");

            var response = await this.ContentMeta.Run(Request("delete"), "alpha", main, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.BadRequest, """
                {"status":"bad_request","subStatus":"","error":"Cannot delete main content."}
                """);
        }

        [Test]
        public async Task Delete_WhileUploadInProgress_Unprocessable()
        {
            await this.CreateSecretAsync("alpha");
            var attachment = await this.CreateAttachmentAsync("alpha");
            await this.UploadChunkAsync("alpha", attachment, Encoding.UTF8.GetBytes("hello"), interim: true);

            var response = await this.ContentMeta.Run(Request("delete"), "alpha", attachment, this.Owner, this.Logger);

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content is being updated."}
                """);
        }
    }
}
