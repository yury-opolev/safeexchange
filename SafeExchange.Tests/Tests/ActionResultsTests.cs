/// <summary>
/// ActionResultsTests
/// </summary>

namespace SafeExchange.Tests
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>
    /// Tests for <see cref="ActionResults.TryCatchAsync"/>, which centralizes the
    /// exception-to-500 conversion for all function handlers. These tests lock in
    /// the OWASP A02:2025 (CWE-209) fix: the response body must NOT contain the
    /// exception type or message — only a correlation ID that operators can use
    /// to join the log entry server-side.
    /// </summary>
    [TestFixture]
    public class ActionResultsTests
    {
        private TestFunctionContext functionContext;
        private TestHttpRequestData request;
        private Mock<ILogger> loggerMock;

        [SetUp]
        public void Setup()
        {
            this.functionContext = new TestFunctionContext
            {
                InstanceServices = BuildWorkerServiceProvider(),
            };
            this.request = new TestHttpRequestData(this.functionContext);
            this.request.SetMethod("GET");
            this.loggerMock = new Mock<ILogger>();
        }

        /// <summary>
        /// HttpResponseDataExtensions.WriteAsJsonAsync resolves an ObjectSerializer
        /// from FunctionContext.InstanceServices — specifically
        /// <see cref="IOptions{WorkerOptions}"/>. Provide a minimal mock service
        /// provider that hands back a <see cref="JsonObjectSerializer"/>.
        /// </summary>
        private static IServiceProvider BuildWorkerServiceProvider()
        {
            var workerOptions = Options.Create(new WorkerOptions { Serializer = new JsonObjectSerializer() });
            var sp = new Mock<IServiceProvider>();
            sp.Setup(x => x.GetService(typeof(IOptions<WorkerOptions>))).Returns(workerOptions);
            return sp.Object;
        }

        [TearDown]
        public void TearDown()
        {
            this.functionContext?.Dispose();
        }

        [Test]
        public async Task TryCatchAsync_ActionSucceeds_ReturnsActionResult()
        {
            // Arrange
            var expected = await ActionResults.CreateResponseAsync(
                this.request,
                HttpStatusCode.OK,
                new BaseResponseObject<string> { Status = "ok", Result = "hello" });

            // Act
            var actual = await ActionResults.TryCatchAsync(
                this.request,
                () => Task.FromResult(expected),
                nameof(TryCatchAsync_ActionSucceeds_ReturnsActionResult),
                this.loggerMock.Object);

            // Assert — the helper must pass the action's result straight through,
            // unchanged, without any exception-handling side effects.
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(actual.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            this.loggerMock.VerifyNoOtherCalls();
        }

        [Test]
        public async Task TryCatchAsync_ActionThrows_Returns500WithGenericError()
        {
            // Arrange
            var thrown = new InvalidOperationException(
                "Cosmos partition key 'secret/tenant-42' not found at endpoint https://prod-cosmos.documents.azure.com");

            // Act
            var response = await ActionResults.TryCatchAsync(
                this.request,
                new Func<Task<HttpResponseData>>(() => throw thrown),
                nameof(TryCatchAsync_ActionThrows_Returns500WithGenericError),
                this.loggerMock.Object);

            // Assert — 500 status with a generic error body. The attacker-visible
            // string must NOT reveal the exception type, message, inner exceptions,
            // Cosmos endpoint, or any part of the stack trace.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));

            var testResponse = (TestHttpResponseData)response;
            var body = testResponse.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body, Is.Not.Null);
            Assert.That(body!.Status, Is.EqualTo("error"));
            Assert.That(body.SubStatus, Is.EqualTo("internal_exception"));
            Assert.That(body.Error, Does.StartWith("Internal error. Reference: "));

            // No leakage of any part of the exception.
            Assert.That(body.Error, Does.Not.Contain("InvalidOperationException"));
            Assert.That(body.Error, Does.Not.Contain("Cosmos"));
            Assert.That(body.Error, Does.Not.Contain("partition key"));
            Assert.That(body.Error, Does.Not.Contain("documents.azure.com"));
            Assert.That(body.Error, Does.Not.Contain("tenant-42"));
        }

        [Test]
        public async Task TryCatchAsync_ActionThrows_CorrelationIdIsValidGuidN()
        {
            // Act
            var response = await ActionResults.TryCatchAsync(
                this.request,
                new Func<Task<HttpResponseData>>(() => throw new Exception("any")),
                nameof(TryCatchAsync_ActionThrows_CorrelationIdIsValidGuidN),
                this.loggerMock.Object);

            // Assert — the Reference must be a 32-char hex identifier (Guid "N" format).
            var testResponse = (TestHttpResponseData)response;
            var body = testResponse.ReadBodyAsJson<BaseResponseObject<object>>();
            var match = Regex.Match(body!.Error, @"Reference:\s+([0-9a-f]{32})$");
            Assert.That(match.Success, Is.True, $"Error body '{body.Error}' did not contain a 32-char hex correlation ID.");
            Assert.That(Guid.TryParseExact(match.Groups[1].Value, "N", out _), Is.True);
        }

        [Test]
        public async Task TryCatchAsync_ActionThrows_LogsErrorWithCorrelationId()
        {
            // Arrange
            var thrown = new InvalidOperationException("secret leak test");

            // Act
            var response = await ActionResults.TryCatchAsync(
                this.request,
                new Func<Task<HttpResponseData>>(() => throw thrown),
                "MyActionName",
                this.loggerMock.Object);

            // Assert — the logger must receive exactly one Error-level call with the
            // actual exception, and the message must mention both the action name and
            // the correlation id that ended up in the client response.
            var testResponse = (TestHttpResponseData)response;
            var body = testResponse.ReadBodyAsJson<BaseResponseObject<object>>();
            var correlationId = Regex.Match(body!.Error, @"[0-9a-f]{32}").Value;

            this.loggerMock.Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) =>
                        v.ToString()!.Contains("MyActionName") &&
                        v.ToString()!.Contains(correlationId)),
                    thrown,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        /// <summary>
        /// <see cref="ActionResults.ForbiddenAsync"/> exists so that the three-line
        /// "return forbidden response" idiom — historically prone to missing the
        /// <c>return</c> keyword (OWASP A01:2025 access-control P0 bypass) — now
        /// collapses into a single expression. This test locks in the response
        /// shape that every call site expects.
        /// </summary>
        [Test]
        public async Task ForbiddenAsync_ReturnsForbiddenBodyWithStatusForbidden()
        {
            // Act
            var response = await ActionResults.ForbiddenAsync(
                this.request, "Applications cannot use this API.");

            // Assert
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var testResponse = (TestHttpResponseData)response;
            var body = testResponse.ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body, Is.Not.Null);
            Assert.That(body!.Status, Is.EqualTo("forbidden"));
            Assert.That(body.Error, Is.EqualTo("Applications cannot use this API."));
            Assert.That(body.SubStatus, Is.EqualTo(string.Empty));
        }

        [Test]
        public async Task ForbiddenAsync_WithSubStatus_PropagatesSubStatus()
        {
            // Act
            var response = await ActionResults.ForbiddenAsync(
                this.request, "Consent required.", "consent_required");

            // Assert
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            var body = ((TestHttpResponseData)response).ReadBodyAsJson<BaseResponseObject<object>>();
            Assert.That(body!.SubStatus, Is.EqualTo("consent_required"));
            Assert.That(body.Error, Is.EqualTo("Consent required."));
        }

        [Test]
        public async Task TryCatchAsync_DifferentFailures_ProduceDistinctCorrelationIds()
        {
            // Act
            var first = await ActionResults.TryCatchAsync(
                this.request,
                new Func<Task<HttpResponseData>>(() => throw new Exception("first")),
                "MyAction",
                this.loggerMock.Object);

            // New request for the second call because TestHttpResponseData writes
            // into its own body MemoryStream that is tied to a single response.
            var secondContext = new TestFunctionContext
            {
                InstanceServices = BuildWorkerServiceProvider(),
            };
            var secondRequest = new TestHttpRequestData(secondContext);
            secondRequest.SetMethod("GET");
            var second = await ActionResults.TryCatchAsync(
                secondRequest,
                new Func<Task<HttpResponseData>>(() => throw new Exception("second")),
                "MyAction",
                this.loggerMock.Object);

            // Assert — the two correlation IDs must be different so a log reader
            // can tell incidents apart.
            var idFirst = Regex.Match(((TestHttpResponseData)first).ReadBodyAsJson<BaseResponseObject<object>>()!.Error, @"[0-9a-f]{32}").Value;
            var idSecond = Regex.Match(((TestHttpResponseData)second).ReadBodyAsJson<BaseResponseObject<object>>()!.Error, @"[0-9a-f]{32}").Value;
            Assert.That(idFirst, Is.Not.EqualTo(idSecond));
        }
    }
}
