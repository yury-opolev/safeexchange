/// <summary>
/// McpOAuthHttpTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// <see cref="McpHttpTestBase"/> with the broker configured (<c>Mcp:OAuth:ClientId</c> and <c>Mcp:OAuth:StateKey</c>),
    /// the <see cref="McpOAuthHandler"/> adapter and a <see cref="FakeEntraTokenClient"/> in place of Entra.
    /// </summary>
    public abstract class McpOAuthHttpTestBase : McpHttpTestBase
    {
        protected const string BrokerClientId = "00000000-0000-0000-0000-0000000000c9";

        protected FakeEntraTokenClient Entra { get; private set; } = null!;

        protected McpOAuthHandler OAuthHandler => this.Services.GetRequiredService<McpOAuthHandler>();

        [SetUp]
        public void OAuthSetUp()
        {
            this.BuildProvider(("Mcp:OAuth:ClientId", BrokerClientId), ("Mcp:OAuth:StateKey", McpOAuthTestKey.Value));
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            this.Entra = new FakeEntraTokenClient();
            services.AddSingleton<IEntraTokenClient>(this.Entra);
            services.AddScoped<McpOAuthHandler>();
        }

        protected static TestHttpRequestData OAuthRequest(string method, string pathAndQuery, string? body = null, string? cookie = null)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetUrl(new Uri(ApiBase, pathAndQuery));
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return request;
        }

        /// <summary>
        /// The <c>name=value</c> part of the response's <c>Set-Cookie</c>, as a browser sends it back.
        /// </summary>
        protected static string CookieFrom(HttpResponseData response)
            => response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }
}
