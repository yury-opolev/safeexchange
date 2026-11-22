/// <summary>
/// McpOAuthHttpTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using Microsoft.Extensions.DependencyInjection;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp.OAuth;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.IO;
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

        protected static TestHttpRequestData OAuthRequest(string method, string pathAndQuery, string? body = null)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetUrl(new Uri(ApiBase, pathAndQuery));
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            return request;
        }
    }
}
