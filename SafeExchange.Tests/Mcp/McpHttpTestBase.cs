/// <summary>
/// McpHttpTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Azure.Core.Serialization;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Functions.Mcp;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Claims;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    /// <summary>
    /// <see cref="McpTestBase"/> plus the Functions adapters (<see cref="McpHandler"/>,
    /// <see cref="OAuthProtectedResourceHandler"/>), the production response serializer settings for REST-shaped
    /// denials, token-shaped principals and a registered application.
    /// </summary>
    public abstract class McpHttpTestBase : McpTestBase
    {
        protected const string AgentClientId = "00000000-0000-0000-0000-0000000000c1";

        protected const string UserObjectId = "00000000-0000-0000-0000-0000000000a1";

        protected static readonly Uri McpUrl = new("https://safeexchange.test/api/v1/mcp");

        private IServiceProvider previousInstanceServices = null!;

        protected McpHandler Handler => this.Services.GetRequiredService<McpHandler>();

        protected OAuthProtectedResourceHandler MetadataHandler => this.Services.GetRequiredService<OAuthProtectedResourceHandler>();

        [SetUp]
        public void HttpSetUp()
        {
            // Mirrors production: CallerDenial responses are written with the worker's JSON serializer.
            this.previousInstanceServices = TestFactory.FunctionContext.InstanceServices;
            var serializer = new JsonObjectSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
            });
            var workerOptions = Options.Create(new WorkerOptions { Serializer = serializer });
            TestFactory.FunctionContext.InstanceServices = Mock.Of<IServiceProvider>(x => x.GetService(typeof(IOptions<WorkerOptions>)) == workerOptions);
        }

        [TearDown]
        public void HttpTearDown()
        {
            TestFactory.FunctionContext.InstanceServices = this.previousInstanceServices;
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<McpHandler>();
            services.AddScoped<OAuthProtectedResourceHandler>();
        }

        protected static ClaimsPrincipal UserPrincipal(string upn)
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("upn", upn),
                new("oid", UserObjectId),
                new("tid", TenantId),
            }.AsEnumerable()));

        // App-only (client-credentials) v2 token shape: azp + azpacr and no scope claim.
        protected static ClaimsPrincipal AppPrincipal(string clientId)
            => new(new CaseSensitiveClaimsIdentity(new List<Claim>
            {
                new("azp", clientId),
                new("azpacr", "1"),
                new("oid", "00000000-0000-0000-0000-0000000000b1"),
                new("tid", TenantId),
            }.AsEnumerable()));

        protected static TestHttpRequestData HttpRequest(string method, string? body = null, string? sessionId = null)
        {
            var request = TestFactory.CreateHttpRequestData(method);
            request.SetUrl(McpUrl);
            request.SetBodyAsStream(new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            if (sessionId is not null)
            {
                request.Headers.Add(McpClientLabel.SessionIdHeaderName, sessionId);
            }

            return request;
        }

        protected static string BodyOf(HttpResponseData response) => ((TestHttpResponseData)response).ReadBodyAsString();

        /// <summary>
        /// Adds the Users row that <see cref="UserPrincipal"/> resolves to (one per test: every user principal
        /// carries <see cref="UserObjectId"/>), with the given MCP settings.
        /// </summary>
        protected async Task<User> SeedMcpUserAsync(string upn, bool mcpAllowed, bool mcpBlockedByAdmin = false)
        {
            var user = new User(upn, UserObjectId, TenantId, upn, upn)
            {
                McpAllowed = mcpAllowed,
                McpBlockedByAdmin = mcpBlockedByAdmin,
            };
            this.DbContext.Users.Add(user);
            await this.DbContext.SaveChangesAsync();
            return user;
        }

        protected async Task SeedApplicationAsync(string clientId, string displayName, bool enabled)
        {
            this.DbContext.Applications.Add(new Application
            {
                Id = Guid.NewGuid().ToString(),
                PartitionKey = Application.DefaultPartitionKey,
                Enabled = enabled,
                DisplayName = displayName,
                ContactEmail = "agent-owner@services.test",
                AadClientId = clientId,
                AadTenantId = TenantId,
                CreatedAt = FixedNow,
                CreatedBy = "mcp-test",
                ModifiedAt = DateTime.MinValue,
                ModifiedBy = string.Empty,
            });
            await this.DbContext.SaveChangesAsync();
        }
    }
}
