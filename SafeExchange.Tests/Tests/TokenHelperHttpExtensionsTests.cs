/// <summary>
/// TokenHelperHttpExtensionsTests
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;

    [TestFixture]
    public class TokenHelperHttpExtensionsTests
    {
        private const string ObjectId = "00000000-0000-0000-0000-0000000000b1";

        private const string TenantId = "00000000-0000-0000-0000-0000000000f1";

        private const string AccountId = ObjectId + "." + TenantId;

        private ITokenHelper tokenHelper = null!;

        [SetUp]
        public void Setup()
        {
            this.tokenHelper = new TokenHelper(new ConfigurationBuilder().Build(), NullLogger<TokenHelper>.Instance);
        }

        [Test]
        public void GetAccountIdAndToken_BearerHeader_ReturnsAccountIdAndToken()
        {
            var request = CreateRequest(instanceServices: null, authorization: "Bearer token-value");

            var result = this.tokenHelper.GetAccountIdAndToken(request, Principal());

            Assert.That(result.AccountId, Is.EqualTo(AccountId));
            Assert.That(result.Token, Is.EqualTo("token-value"));
        }

        [TestCase(null)]
        [TestCase("Basic dXNlcjpwYXNz")]
        [TestCase("Bearer")]
        public void GetAccountIdAndToken_NoBearerToken_ReturnsEmptyToken(string? authorization)
        {
            var request = CreateRequest(instanceServices: null, authorization);

            var result = this.tokenHelper.GetAccountIdAndToken(request, Principal());

            Assert.That(result.AccountId, Is.EqualTo(AccountId));
            Assert.That(result.Token, Is.Empty);
        }

        [Test]
        public void GetAccountIdAndToken_LogsUnderTheTokenHelperCategory()
        {
            var log = new RecordingLogger();
            var categories = new List<string>();
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory
                .Setup(f => f.CreateLogger(It.IsAny<string>()))
                .Callback<string>(categories.Add)
                .Returns(log);
            var instanceServices = new Mock<IServiceProvider>();
            instanceServices
                .Setup(s => s.GetService(typeof(ILoggerFactory)))
                .Returns(loggerFactory.Object);
            var request = CreateRequest(instanceServices.Object, "Bearer token-value");

            this.tokenHelper.GetAccountIdAndToken(request, Principal());

            Assert.That(categories, Is.EqualTo(new[] { "SafeExchange.Core.TokenHelper" }));
            Assert.That(
                log.Entries.Select(e => $"{e.Level} {e.Message}"),
                Is.EqualTo(new[] { $"Information GetAccountIdAndToken: AccountId [{AccountId}], token extracted" }));
        }

        private static TestHttpRequestData CreateRequest(IServiceProvider? instanceServices, string? authorization)
        {
            var request = new TestHttpRequestData(new TestFunctionContext { InstanceServices = instanceServices! });
            request.SetMethod("get");
            if (authorization is not null)
            {
                request.Headers.Add("Authorization", authorization);
            }

            return request;
        }

        private static ClaimsPrincipal Principal()
            => new(new ClaimsIdentity(new[]
            {
                new Claim("oid", ObjectId),
                new Claim("tid", TenantId),
            }));
    }
}
