/// <summary>
/// McpTestBase
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using NUnit.Framework;
    using SafeExchange.Core;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Configuration;
    using SafeExchange.Core.Groups;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Core.Permissions;
    using SafeExchange.Core.Services;
    using SafeExchange.Tests.Services;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;

    /// <summary>
    /// MCP tools over the real services (EF InMemory, in-memory blobs, real audit writer and permissions): a DI
    /// container registered like production (<c>AddSafeExchangeMcp</c>, the real <see cref="CallerResolver"/>),
    /// with every log entry of every category captured in <see cref="Logs"/>.
    /// </summary>
    public abstract class McpTestBase : ServiceTestBase
    {
        protected const string Label = "copilot-cli/1.0.114";

        protected const string TenantId = "00000000-0000-0000-0000-000000000001";

        protected static readonly Uri ApiBase = new("https://safeexchange.test/api/");

        private ServiceProvider? provider;

        private IServiceScope? scope;

        protected RecordingLoggerProvider Logs { get; private set; } = null!;

        protected IServiceProvider Services => this.scope!.ServiceProvider;

        protected McpRequestDispatcher Dispatcher => this.Services.GetRequiredService<McpRequestDispatcher>();

        protected Caller McpOwner { get; } = McpCaller(OwnerUpn);

        protected Caller McpReader { get; } = McpCaller(ReaderUpn);

        protected Caller McpStranger { get; } = McpCaller(StrangerUpn);

        [SetUp]
        public void McpSetUp()
        {
            this.Logs = new RecordingLoggerProvider();
            this.BuildProvider();
        }

        [TearDown]
        public void McpTearDown()
        {
            this.scope?.Dispose();
            this.provider?.Dispose();
        }

        protected static Caller McpCaller(string upn, string? label = Label)
            => new(SubjectType.User, upn, upn, Array.Empty<string>(), CallerChannels.Mcp, label);

        /// <summary>
        /// (Re)builds the container over the same database and blobs; overrides change configuration values,
        /// e.g. <c>("Features:McpEndpoint", "False")</c>.
        /// </summary>
        protected void BuildProvider(params (string Key, string? Value)[] overrides)
        {
            this.scope?.Dispose();
            this.provider?.Dispose();

            var settings = new Dictionary<string, string?>
            {
                { "Features:McpEndpoint", "True" },
                { "Authentication:Authority", $"https://login.microsoftonline.com/{TenantId}" },
                { "Authentication:ValidAudiences", "00000000-0000-0000-0000-0000000000e1,api://safeexchange.test" },
            };
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }

            var configuration = new ConfigurationBuilder()
                .AddConfiguration(this.Configuration)
                .AddInMemoryCollection(settings)
                .Build();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(this.Logs).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<IConfiguration>(configuration);
            services.Configure<Features>(configuration.GetSection("Features"));
            services.Configure<AuthenticationConfiguration>(configuration.GetSection("Authentication"));
            services.Configure<OrphanedSecretConfiguration>(configuration.GetSection("OrphanedSecret"));
            services.Configure<GloballyAllowedGroupsConfiguration>(configuration.GetSection("GlobalAllowLists"));

            services.AddSingleton(this.DbContext);
            services.AddSingleton(this.Purger);
            services.AddSingleton(this.PermissionsManager);
            services.AddSingleton(this.AuditWriter);
            services.AddSingleton<IBlobHelper>(this.BlobHelper);
            services.AddSingleton<IGroupsManager>(new GroupsManager(this.DbContext, NullLogger<GroupsManager>.Instance));
            services.AddSingleton<IOrphanedSecretManager, OrphanedSecretManager>();
            services.AddSingleton<ITokenHelper>(new TokenHelper(configuration, NullLogger<TokenHelper>.Instance));
            services.AddScoped<ICallerResolver, CallerResolver>();
            services.AddScoped<SecretService>();
            services.AddScoped<ContentService>();
            services.AddScoped<ChunkService>();
            services.AddScoped<AccessService>();
            services.AddScoped<AuditQueryService>();
            services.AddSafeExchangeMcp();
            this.ConfigureServices(services);

            this.provider = services.BuildServiceProvider();
            this.scope = this.provider.CreateScope();
        }

        /// <summary>
        /// Adds host registrations (e.g. the Functions handlers) to the container.
        /// </summary>
        protected virtual void ConfigureServices(IServiceCollection services)
        {
        }

        protected Task<McpResponse> RpcAsync(string body, Caller? caller = null, string? protocolVersion = null)
            => this.Dispatcher.DispatchAsync(new McpRequest(body, caller ?? this.McpOwner, ApiBase, protocolVersion));

        protected static string Rpc(string method, object? parameters = null)
        {
            var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            return JsonSerializer.Serialize(message);
        }

        /// <summary>
        /// Calls a tool through the dispatcher and returns the JSON-RPC <c>result</c> (a CallToolResult).
        /// </summary>
        protected async Task<JsonObject> CallToolAsync(Caller caller, string tool, object? arguments = null)
        {
            var response = await this.RpcAsync(Rpc("tools/call", new { name = tool, arguments = arguments ?? new { } }), caller);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), response.Body);
            var message = JsonNode.Parse(response.Body!)!.AsObject();
            Assert.That(message["error"], Is.Null, response.Body);
            return message["result"]!.AsObject();
        }

        /// <summary>
        /// Asserts a successful tool result and returns its structured content.
        /// </summary>
        protected static JsonObject Ok(JsonObject result)
        {
            Assert.That(result["isError"]!.GetValue<bool>(), Is.False, result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }

        protected static void AssertToolError(JsonObject result, string status, string error)
        {
            Assert.That(result["isError"]!.GetValue<bool>(), Is.True, result.ToJsonString());
            Assert.That(result["structuredContent"]!.ToJsonString(), Is.EqualTo(new JsonObject { ["status"] = status, ["error"] = error }.ToJsonString()));
        }

        protected static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

        protected static object Expiration() => new
        {
            scheduleExpiration = false,
            expireAt = "2027-01-01T00:00:00Z",
            expireOnIdleTime = false,
            idleTimeToExpire = "180.00:00:00",
        };
    }
}
