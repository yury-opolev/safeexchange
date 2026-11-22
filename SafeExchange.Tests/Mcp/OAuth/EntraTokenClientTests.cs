/// <summary>
/// EntraTokenClientTests
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    [TestFixture]
    public class EntraTokenClientTests
    {
        private const string Tenant = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001";

        private const string Code = "1.AQEA-secret-code-for-the-client-log-test";

        private const string Verifier = "verifier-for-the-client-log-test-0123456789abcdef";

        [Test]
        public async Task Redeem_PostsTheFormToTheFixedTokenEndpoint_AndReturnsEntrasAnswer()
        {
            var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(EntraTokenClient.HttpClientName)).Returns(() => new HttpClient(handler));
            var authentication = Mock.Of<IOptionsMonitor<AuthenticationConfiguration>>(x => x.CurrentValue == new AuthenticationConfiguration { Authority = Tenant + "/v2.0" });
            var client = new EntraTokenClient(factory.Object, authentication);

            var response = await client.RedeemAsync(new KeyValuePair<string, string>[] { new("grant_type", "refresh_token"), new("refresh_token", "r t+/=") });

            Assert.That(response, Is.EqualTo(new EntraTokenResponse(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")));
            Assert.That(handler.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.RequestUri, Is.EqualTo(new Uri(Tenant + "/oauth2/v2.0/token")));
            Assert.That(handler.ContentType, Is.EqualTo("application/x-www-form-urlencoded"));
            Assert.That(handler.Body, Is.EqualTo("grant_type=refresh_token&refresh_token=r+t%2B%2F%3D"));
        }

        [Test]
        public void AddSafeExchangeMcp_RegistersTheBroker_AndATenSecondEntraClientThatDoesNotFollowRedirects()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSafeExchangeMcp();
            using var provider = services.BuildServiceProvider();

            Assert.That(provider.GetRequiredService<IEntraTokenClient>(), Is.InstanceOf<EntraTokenClient>());
            Assert.That(provider.GetRequiredService<McpOAuthBroker>(), Is.SameAs(provider.GetRequiredService<McpOAuthBroker>()));
            Assert.That(provider.GetRequiredService<IHttpClientFactory>().CreateClient(EntraTokenClient.HttpClientName).Timeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(EntraTokenClient.HttpClientName);
            while (handler is DelegatingHandler delegating)
            {
                handler = delegating.InnerHandler!;
            }

            Assert.That(handler, Is.InstanceOf<SocketsHttpHandler>());
            Assert.That(((SocketsHttpHandler)handler).AllowAutoRedirect, Is.False);
        }

        [Test]
        public async Task TheEntraCall_LogsNoFormValueOrResponseBody()
        {
            var logs = new RecordingLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
            services.Configure<AuthenticationConfiguration>(c => c.Authority = Tenant);
            services.AddSafeExchangeMcp();
            services.AddHttpClient(EntraTokenClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, $"{{\"access_token\":\"{FakeEntraTokenClient.AccessToken}\"}}"));
            using var provider = services.BuildServiceProvider();

            await provider.GetRequiredService<IEntraTokenClient>().RedeemAsync(new KeyValuePair<string, string>[]
            {
                new("grant_type", "authorization_code"),
                new("code", Code),
                new("code_verifier", Verifier),
            });

            var text = string.Join('\n', logs.Entries);
            Assert.That(text, Does.Contain("/oauth2/v2.0/token"), "Arrange: the HttpClient logged the call.");
            foreach (var forbidden in new[] { Code, Verifier, FakeEntraTokenClient.AccessToken })
            {
                Assert.That(text, Does.Not.Contain(forbidden), $"'{forbidden}' was logged.");
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode statusCode;

            private readonly string responseBody;

            public StubHandler(HttpStatusCode statusCode, string responseBody)
            {
                this.statusCode = statusCode;
                this.responseBody = responseBody;
            }

            public HttpMethod? Method { get; private set; }

            public Uri? RequestUri { get; private set; }

            public string? ContentType { get; private set; }

            public string? Body { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                this.Method = request.Method;
                this.RequestUri = request.RequestUri;
                this.ContentType = request.Content?.Headers.ContentType?.MediaType;
                this.Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(this.statusCode) { Content = new StringContent(this.responseBody) };
            }
        }
    }
}
