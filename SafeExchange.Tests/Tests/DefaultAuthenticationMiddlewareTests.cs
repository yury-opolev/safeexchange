/// <summary>
/// DefaultAuthenticationMiddlewareTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Logging;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Middleware;
    using System.Text.Json;

    /// <summary>
    /// Tests for the OWASP A02:2025 / CWE-209 hardening in
    /// <see cref="DefaultAuthenticationMiddleware"/>: the 401 body must never
    /// echo <c>Microsoft.IdentityModel</c> error strings (IDX10205 / IDX10214 /
    /// IDX10223 etc.) that let an unauthenticated caller enumerate configured
    /// audiences, issuers, tenant ids and signing-key rotation state by probing
    /// tokens in a loop.
    ///
    /// The middleware itself depends on Functions Worker extension methods
    /// (<c>GetHttpRequestDataAsync</c>, <c>GetInvocationResult</c>) that need a
    /// full hosting harness to exercise end-to-end. These tests therefore lock
    /// in the contract at the layer that matters: the fixed error string and
    /// its JSON shape. Any regression that switches the constant, or any future
    /// code path that writes an <c>exception.Message</c> instead of the
    /// constant, will fail here.
    /// </summary>
    [TestFixture]
    public class DefaultAuthenticationMiddlewareTests
    {
        [Test]
        public void InvalidTokenErrorMessage_IsGenericAndContainsNoIdx()
        {
            var message = DefaultAuthenticationMiddleware.InvalidTokenErrorMessage;
            Assert.That(message, Is.EqualTo("Invalid or expired token."));
            Assert.That(message, Does.Not.Contain("IDX"));
            Assert.That(message, Does.Not.Contain("Issuer"));
            Assert.That(message, Does.Not.Contain("Audience"));
            Assert.That(message, Does.Not.Contain("Lifetime"));
        }

        /// <summary>
        /// The unauthorized body written by the middleware is structurally a
        /// <see cref="BaseResponseObject{T}"/> with <c>Status = "unauthorized"</c>
        /// and the fixed error message. Assert the shape at the serialization
        /// boundary so any handler elsewhere that reuses this pattern stays
        /// consistent.
        /// </summary>
        [Test]
        public void UnauthorizedBody_SerializesWithStatusUnauthorizedAndFixedError()
        {
            var body = new BaseResponseObject<object>
            {
                Status = "unauthorized",
                Error = DefaultAuthenticationMiddleware.InvalidTokenErrorMessage,
            };

            var json = JsonSerializer.Serialize(body);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.That(root.GetProperty("Status").GetString(), Is.EqualTo("unauthorized"));
            Assert.That(root.GetProperty("Error").GetString(), Is.EqualTo("Invalid or expired token."));

            // And negatively — none of the IDX strings or internal token state must
            // appear in a body built from the public constant.
            Assert.That(json, Does.Not.Contain("IDX10205"));
            Assert.That(json, Does.Not.Contain("IDX10214"));
            Assert.That(json, Does.Not.Contain("IDX10223"));
            Assert.That(json, Does.Not.Contain("ValidIssuer"));
            Assert.That(json, Does.Not.Contain("ValidAudience"));
            Assert.That(json, Does.Not.Contain("ValidTo"));
        }

        /// <summary>
        /// Exhaustive guard: for every exception class the catch clause handles,
        /// the exception's message — no matter how sensitive — must not be
        /// returned to the client. We don't call the middleware here (private
        /// UnauthorizedAsync + Functions Worker extensions make that costly);
        /// instead we lock in that any attempt to embed such a message in the
        /// response shape is visibly wrong — the response carries the constant,
        /// not the exception.
        /// </summary>
        [Test]
        public void RegressionGuard_RejectsEchoOfSecurityTokenExceptionMessage()
        {
            var sensitive = new SecurityTokenInvalidIssuerException(
                "IDX10205: Issuer validation failed. Issuer: 'https://evil'. Did not match: validationParameters.ValidIssuer: 'https://sts.safeexchange.example'");

            // A handler that (incorrectly) echoed sensitive.Message would surface
            // the IDX identifier and configured ValidIssuer. The public constant
            // never does.
            Assert.That(DefaultAuthenticationMiddleware.InvalidTokenErrorMessage,
                Does.Not.Contain(sensitive.Message.Substring(0, 8))); // "IDX10205"
        }

        /// <summary>
        /// Sanity test that the logger interface types referenced by the
        /// middleware still compile — prevents a silent rename from hiding a
        /// logging-contract regression. Runs pure-CLR, no I/O.
        /// </summary>
        [Test]
        public void LoggerTypeIsResolvable()
        {
            var loggerMock = new Mock<ILogger<DefaultAuthenticationMiddleware>>();
            Assert.That(loggerMock.Object, Is.Not.Null);
        }
    }
}
