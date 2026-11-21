/// <summary>
/// ServiceResultTests
/// </summary>

namespace SafeExchange.Tests.Services
{
    using NUnit.Framework;
    using SafeExchange.Core.Model.Dto.Output;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using System;
    using System.Net;
    using System.Threading.Tasks;

    [TestFixture]
    public class ServiceResultTests
    {
        [TestCase(ServiceStatus.Ok, "ok")]
        [TestCase(ServiceStatus.NotFound, "not_found")]
        [TestCase(ServiceStatus.Forbidden, "forbidden")]
        [TestCase(ServiceStatus.Unauthorized, "unauthorized")]
        [TestCase(ServiceStatus.Unprocessable, "unprocessable")]
        [TestCase(ServiceStatus.BadRequest, "bad_request")]
        [TestCase(ServiceStatus.Conflict, "conflict")]
        [TestCase(ServiceStatus.Error, "error")]
        [TestCase(ServiceStatus.ChunkHashMismatch, "chunk_hash_mismatch")]
        [TestCase(ServiceStatus.HashMismatch, "hash_mismatch")]
        [TestCase(ServiceStatus.NoUploadState, "no_upload_state")]
        [TestCase(ServiceStatus.InconsistentUploadMode, "inconsistent_upload_mode")]
        public void ToStatusString_IsTheRestStatusString(ServiceStatus status, string expected)
        {
            Assert.That(status.ToStatusString(), Is.EqualTo(expected));
        }

        [Test]
        public void Shortcuts_CarryTodaysStatusCodes()
        {
            AssertFailure(ServiceResult.NotFound("n"), ServiceStatus.NotFound, HttpStatusCode.NotFound, "n", string.Empty);
            AssertFailure(ServiceResult.Forbidden("f", "consent_required"), ServiceStatus.Forbidden, HttpStatusCode.Forbidden, "f", "consent_required");
            AssertFailure(ServiceResult.Unauthorized("u"), ServiceStatus.Unauthorized, HttpStatusCode.Unauthorized, "u", string.Empty);
            AssertFailure(ServiceResult.Unprocessable("p"), ServiceStatus.Unprocessable, HttpStatusCode.UnprocessableEntity, "p", string.Empty);
            AssertFailure(ServiceResult.BadRequest("b"), ServiceStatus.BadRequest, HttpStatusCode.BadRequest, "b", string.Empty);
            AssertFailure(ServiceResult.Conflict("c"), ServiceStatus.Conflict, HttpStatusCode.Conflict, "c", string.Empty);
        }

        [Test]
        public void InsufficientPermissions_UsesTodaysMessage()
        {
            var result = ServiceResult.InsufficientPermissions(PermissionType.GrantAccess, "alpha", "consent_required");

            AssertFailure(result, ServiceStatus.Forbidden, HttpStatusCode.Forbidden, "Insufficient permissions to do 'GrantAccess' action on 'alpha'", "consent_required");
        }

        [Test]
        public void Failure_KeepsDetail_AndRejectsOk()
        {
            var detail = new ChunkHashMismatch { Expected = "a", Actual = "b" };

            var result = ServiceResult.Failure(ServiceStatus.ChunkHashMismatch, HttpStatusCode.UnprocessableEntity, "m", detail: detail);

            Assert.That(result.Detail, Is.SameAs(detail));
            Assert.Throws<ArgumentException>(() => ServiceResult.Failure(ServiceStatus.Ok, HttpStatusCode.OK, "x"));
        }

        [Test]
        public void GenericOk_CarriesValue()
        {
            var result = ServiceResult<string>.Ok("value");

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Value, Is.EqualTo("value"));
            Assert.That(result.Status, Is.EqualTo(ServiceStatus.Ok));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void Failure_ConvertsToGenericResult_ButOkDoesNot()
        {
            ServiceResult<string> converted = ServiceResult.NotFound("missing");

            Assert.That(converted.IsOk, Is.False);
            Assert.That(converted.Value, Is.Null);
            Assert.That(converted.Error, Is.EqualTo("missing"));
            Assert.Throws<InvalidOperationException>(() => { ServiceResult<string> _ = ServiceResult.Ok(); });
        }

        [Test]
        public void ServiceOperation_WrapsExceptions_Once()
        {
            var inner = new InvalidOperationException("boom");

            var wrapped = Assert.ThrowsAsync<ServiceOperationException>(() => ServiceOperation.RunAsync<int>(() => throw inner));
            Assert.That(wrapped!.InnerException, Is.SameAs(inner));

            var rethrown = Assert.ThrowsAsync<ServiceOperationException>(() => ServiceOperation.RunAsync(() => throw wrapped));
            Assert.That(rethrown, Is.SameAs(wrapped));
        }

        [Test]
        public async Task ServiceOperation_ReturnsTheOperationResult()
        {
            Assert.That(await ServiceOperation.RunAsync(() => Task.FromResult(42)), Is.EqualTo(42));
        }

        private static void AssertFailure(ServiceResult result, ServiceStatus status, HttpStatusCode statusCode, string error, string subStatus)
        {
            Assert.That(result.IsOk, Is.False);
            Assert.That(result.Status, Is.EqualTo(status));
            Assert.That(result.StatusCode, Is.EqualTo(statusCode));
            Assert.That(result.Error, Is.EqualTo(error));
            Assert.That(result.SubStatus, Is.EqualTo(subStatus));
            Assert.That(result.Detail, Is.Null);
        }
    }
}
