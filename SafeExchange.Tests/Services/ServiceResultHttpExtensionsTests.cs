/// <summary>
/// ServiceResultHttpExtensionsTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Tests.Contract;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Uses the contract fixture plumbing (production serializer settings) so the bytes compare with what
    /// the handlers wrote before they became adapters.
    /// </summary>
    [TestFixture]
    public class ServiceResultHttpExtensionsTests : ContractTestBase
    {
        [Test]
        public async Task Failure_IsByteIdenticalToTheHandlersJson()
        {
            var expected = await ActionResults.CreateResponseAsync(
                Request("get"), HttpStatusCode.NotFound,
                new BaseResponseObject<object> { Status = "not_found", Error = "Secret 'alpha' does not exist." });

            var actual = await ServiceResult.NotFound("Secret 'alpha' does not exist.").ToResponseAsync(Request("get"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task InsufficientPermissions_IsByteIdenticalToActionResults()
        {
            var expected = await ActionResults.CreateResponseAsync(
                Request("post"), HttpStatusCode.Forbidden,
                ActionResults.InsufficientPermissions(PermissionType.GrantAccess, "alpha", "consent_required"));

            ServiceResult<string> failure = ServiceResult.InsufficientPermissions(PermissionType.GrantAccess, "alpha", "consent_required");
            var actual = await failure.ToResponseAsync(Request("post"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task FailureWithDetail_IsByteIdenticalToTheTypedResultObject()
        {
            var mismatch = new ChunkHashMismatch { Expected = "aa", Actual = "bb" };
            var expected = await ActionResults.CreateResponseAsync(
                Request("post"), HttpStatusCode.UnprocessableEntity,
                new BaseResponseObject<ChunkHashMismatch> { Status = "chunk_hash_mismatch", Error = "Received chunk bytes do not match the client-asserted hash.", Result = mismatch });

            var actual = await ServiceResult.Failure(
                ServiceStatus.ChunkHashMismatch, HttpStatusCode.UnprocessableEntity,
                "Received chunk bytes do not match the client-asserted hash.", detail: mismatch).ToResponseAsync(Request("post"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task Ok_WithoutValue_WritesResultOk()
        {
            var response = await ServiceResult.Ok().ToResponseAsync(Request("delete"));

            ContractAssert.Json(response, HttpStatusCode.OK, """
                {"status":"ok","subStatus":"","result":"ok"}
                """);
        }

        [Test]
        public async Task Ok_WithValue_IsByteIdenticalToTheTypedResultObject()
        {
            var value = new List<string> { "a", "b" };
            var expected = await ActionResults.CreateResponseAsync(
                Request("get"), HttpStatusCode.OK, new BaseResponseObject<List<string>> { Status = "ok", Result = value });

            var actual = await ServiceResult<List<string>>.Ok(value).ToResponseAsync(Request("get"));

            AssertSameResponse(actual, expected);
        }

        [Test]
        public async Task Download_WritesHeadersInOrderThenTheBytes()
        {
            var bytes = Encoding.UTF8.GetBytes("hello");
            var download = new ContentDownload("text/plain", bytes.Length, "attachment; filename=a.txt", (stream, ct) => stream.WriteAsync(bytes, 0, bytes.Length, ct));

            var response = await ServiceResult<ContentDownload>.Ok(download).ToDownloadResponseAsync(Request("get"));

            ContractAssert.Bytes(response, HttpStatusCode.OK, bytes,
                "Content-Type: text/plain", "Content-Length: 5", "Content-Disposition: attachment; filename=a.txt");
        }

        [Test]
        public async Task Download_WithoutDisposition_HasNoDispositionHeader()
        {
            var download = new ContentDownload("text/plain", 0, null, (stream, ct) => Task.CompletedTask);

            var response = await ServiceResult<ContentDownload>.Ok(download).ToDownloadResponseAsync(Request("get"));

            ContractAssert.Bytes(response, HttpStatusCode.OK, Array.Empty<byte>(), "Content-Type: text/plain", "Content-Length: 0");
        }

        [Test]
        public async Task Download_Failure_IsWrittenAsJson()
        {
            ServiceResult<ContentDownload> failure = ServiceResult.Unprocessable("Content 'c' is not ready.");

            var response = await failure.ToDownloadResponseAsync(Request("get"));

            ContractAssert.Json(response, HttpStatusCode.UnprocessableEntity, """
                {"status":"unprocessable","subStatus":"","error":"Content 'c' is not ready."}
                """);
        }

        [Test]
        public async Task ServiceCallAsync_TurnsOperationFailuresIntoTheInternalErrorResponse()
        {
            var response = await ActionResults.ServiceCallAsync(
                Request("get"),
                () => throw new ServiceOperationException(new InvalidOperationException("boom")),
                "HandleSomething",
                this.Logger);

            ContractAssert.Json(response, HttpStatusCode.InternalServerError, """
                {"status":"error","subStatus":"internal_exception","error":"Internal error. Reference: <correlationId>"}
                """);
        }

        [Test]
        public void ServiceCallAsync_LetsOtherExceptionsPropagate()
        {
            var thrown = new InvalidOperationException("outside the operation");

            var actual = Assert.ThrowsAsync<InvalidOperationException>(() => ActionResults.ServiceCallAsync(
                Request("get"), () => throw thrown, "HandleSomething", this.Logger));

            Assert.That(actual, Is.SameAs(thrown));
        }

        private static void AssertSameResponse(HttpResponseData actual, HttpResponseData expected)
        {
            Assert.That(actual.StatusCode, Is.EqualTo(expected.StatusCode));
            Assert.That(ContractAssert.HeaderLines(actual), Is.EqualTo(ContractAssert.HeaderLines(expected)));
            Assert.That(ContractAssert.ReadBodyBytes(actual), Is.EqualTo(ContractAssert.ReadBodyBytes(expected)));
        }
    }
}
