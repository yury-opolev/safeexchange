/// <summary>
/// McpRequestDispatcherTests
/// </summary>

namespace SafeExchange.Tests.Mcp
{
    using Microsoft.Extensions.Logging;
    using ModelContextProtocol.Protocol;
    using NUnit.Framework;
    using SafeExchange.Core.Callers;
    using SafeExchange.Core.Mcp;
    using SafeExchange.Core.Model;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// JSON-RPC handling of the stateless endpoint (one message in, one JSON response out), with two fake tools.
    /// </summary>
    [TestFixture]
    public class McpRequestDispatcherTests
    {
        private static readonly Caller Agent = new(SubjectType.Application, "agent", "agent", Array.Empty<string>(), CallerChannels.Mcp, "test/1");

        private RecordingLoggerProvider logs = null!;

        private McpRequestDispatcher dispatcher = null!;

        [SetUp]
        public void SetUp()
        {
            this.logs = new RecordingLoggerProvider();
            var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(this.logs).SetMinimumLevel(LogLevel.Trace));
            this.dispatcher = new McpRequestDispatcher(
                new McpToolRegistry(new IMcpTool[] { new EchoTool(), new ThrowingTool() }),
                loggerFactory.CreateLogger<McpRequestDispatcher>());
        }

        [Test]
        public async Task Initialize_EchoesASupportedVersion_AndReturnsTheClientLabelAsSessionId()
        {
            var response = await this.DispatchAsync(Rpc("initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "copilot-cli", version = "1.0.114" },
            }));

            var result = Message(response)["result"]!;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result["protocolVersion"]!.GetValue<string>(), Is.EqualTo("2025-06-18"));
            Assert.That(result["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("safeexchange"));
            Assert.That(result["capabilities"]!.ToJsonString(), Is.EqualTo("""{"tools":{}}"""));
            Assert.That(result["instructions"]!.GetValue<string>(), Does.Contain("file_transfer_info"));
            Assert.That(response.SessionId, Is.EqualTo("Y29waWxvdC1jbGkvMS4wLjExNA"));
        }

        [Test]
        public async Task Initialize_UnsupportedVersion_AnswersTheLatest()
        {
            var response = await this.DispatchAsync(Rpc("initialize", new
            {
                protocolVersion = "1999-01-01",
                capabilities = new { },
                clientInfo = new { name = "old", version = "1" },
            }));

            Assert.That(Message(response)["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo("2025-11-25"));
        }

        [Test]
        public async Task Initialize_WithoutClientInfo_IsInvalidParams()
        {
            var response = await this.DispatchAsync(Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { } }));

            AssertRpcError(response, HttpStatusCode.OK, 1, -32602);
            Assert.That(response.SessionId, Is.Null);
        }

        [Test]
        public async Task Ping_ReturnsAnEmptyResult()
        {
            var response = await this.DispatchAsync(Rpc("ping", id: "p-1"));

            var message = Message(response);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(message["id"]!.GetValue<string>(), Is.EqualTo("p-1"));
            Assert.That(message["jsonrpc"]!.GetValue<string>(), Is.EqualTo("2.0"));
            Assert.That(message["result"]!.ToJsonString(), Is.EqualTo("{}"));
        }

        [Test]
        public async Task ToolsList_ListsTheRegisteredTools()
        {
            var response = await this.DispatchAsync(Rpc("tools/list"));

            Assert.That(Message(response)["result"]!["tools"]!.ToJsonString(), Is.EqualTo(
                """[{"name":"echo","description":"Echoes value.","inputSchema":{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]},"annotations":{"openWorldHint":false,"readOnlyHint":true}},"""
                + """{"name":"explode","description":"Always fails.","inputSchema":{"type":"object"},"annotations":{"destructiveHint":false,"openWorldHint":false,"readOnlyHint":false}}]"""));
        }

        [Test]
        public async Task UnknownMethod_IsMethodNotFound()
        {
            var response = await this.DispatchAsync(Rpc("resources/list", id: 9));

            AssertRpcError(response, HttpStatusCode.OK, 9, -32601);
        }

        [Test]
        public async Task NotificationsAndResponses_AreAcceptedWithoutABody()
        {
            var initialized = await this.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            var cancelled = await this.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}""");
            var clientResponse = await this.DispatchAsync("""{"jsonrpc":"2.0","id":4,"result":{}}""");

            foreach (var response in new[] { initialized, cancelled, clientResponse })
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
                Assert.That(response.Body, Is.Null);
            }
        }

        [Test]
        public async Task InvalidJson_IsAParseError()
        {
            var response = await this.DispatchAsync("{not json");

            AssertRpcError(response, HttpStatusCode.BadRequest, null, -32700);
        }

        [Test]
        public async Task BatchArray_IsRejected()
        {
            var response = await this.DispatchAsync($"[{Rpc("ping")}]");

            AssertRpcError(response, HttpStatusCode.BadRequest, null, -32600);
            Assert.That(Message(response)["error"]!["message"]!.GetValue<string>(), Is.EqualTo("Batch requests are not supported."));
        }

        [TestCase("""{"jsonrpc":"1.0","id":5,"method":"ping"}""", 5)]
        [TestCase("""{"id":5,"method":"ping"}""", 5)]
        [TestCase("""{"jsonrpc":"2.0","id":5,"method":7}""", 5)]
        [TestCase("""42""", null)]
        public async Task NotAJsonRpcMessage_IsAnInvalidRequest(string body, int? id)
        {
            var response = await this.DispatchAsync(body);

            AssertRpcError(response, HttpStatusCode.BadRequest, id, -32600);
        }

        [Test]
        public async Task UnsupportedProtocolVersionHeader_IsABadRequest()
        {
            var unsupported = await this.DispatchAsync(Rpc("ping"), protocolVersion: "2024-01-01");
            var supported = await this.DispatchAsync(Rpc("ping"), protocolVersion: "2025-06-18");

            AssertRpcError(unsupported, HttpStatusCode.BadRequest, null, -32600);
            Assert.That(supported.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task ToolsCall_UnknownToolOrMissingName_IsInvalidParams()
        {
            var unknown = await this.DispatchAsync(Rpc("tools/call", new { name = "secret_steal" }));
            var unnamed = await this.DispatchAsync(Rpc("tools/call", new { arguments = new { } }));

            AssertRpcError(unknown, HttpStatusCode.OK, 1, -32602);
            Assert.That(Message(unknown)["error"]!["message"]!.GetValue<string>(), Is.EqualTo("Unknown tool: 'secret_steal'."));
            AssertRpcError(unnamed, HttpStatusCode.OK, 1, -32602);
        }

        [Test]
        public async Task ToolsCall_RunsTheToolWithTheCallerAndArguments_AndLogsNoArgumentValues()
        {
            var response = await this.DispatchAsync(Rpc("tools/call", new { name = "echo", arguments = new { name = "alpha", value = "do-not-log-me" } }));

            var result = Message(response)["result"]!;
            Assert.That(result["isError"]!.GetValue<bool>(), Is.False);
            Assert.That(result["structuredContent"]!.ToJsonString(), Is.EqualTo("""{"caller":"agent","untrusted":{"value":"do-not-log-me"}}"""));
            Assert.That(result["content"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
            var logs = string.Join('\n', this.logs.Entries);
            Assert.That(logs, Does.Contain("MCP tool echo on secret 'alpha' finished with ok in "));
            Assert.That(logs, Does.Not.Contain("do-not-log-me"));
        }

        [Test]
        public async Task ToolsCall_ArgumentsOfTheWrongType_AreAToolError()
        {
            var response = await this.DispatchAsync(Rpc("tools/call", new { name = "echo", arguments = new { value = 5 } }));

            var result = Message(response)["result"]!;
            Assert.That(result["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(result["structuredContent"]!.ToJsonString(), Is.EqualTo("""{"status":"invalid_arguments","error":"\u0027value\u0027 must be a string."}"""));
        }

        [Test]
        public async Task ToolsCall_UnexpectedFailure_IsAnInternalErrorWithALoggedReference()
        {
            var response = await this.DispatchAsync(Rpc("tools/call", new { name = "explode", arguments = new { name = "alpha" } }));

            var result = Message(response)["result"]!;
            var error = result["structuredContent"]!["error"]!.GetValue<string>();
            Assert.That(result["isError"]!.GetValue<bool>(), Is.True);
            Assert.That(result["structuredContent"]!["status"]!.GetValue<string>(), Is.EqualTo("error"));
            Assert.That(error, Does.Match("^Internal error. Reference: [0-9a-f]{32}$"));
            var reference = error.Substring(error.Length - 32);
            Assert.That(this.logs.Entries.Any(e => e.StartsWith("Error") && e.Contains(reference) && e.Contains("boom")), Is.True);
            Assert.That(this.logs.Entries.Any(e => e.Contains("MCP tool explode on secret 'alpha' finished with error")), Is.True);
        }

        private Task<McpResponse> DispatchAsync(string body, string? protocolVersion = null)
            => this.dispatcher.DispatchAsync(new McpRequest(body, Agent, new Uri("https://safeexchange.test/api/"), protocolVersion));

        private static string Rpc(string method, object? parameters = null, object? id = null)
        {
            var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id ?? 1, ["method"] = method };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            return JsonSerializer.Serialize(message);
        }

        private static JsonObject Message(McpResponse response) => JsonNode.Parse(response.Body!)!.AsObject();

        private static void AssertRpcError(McpResponse response, HttpStatusCode statusCode, int? id, int code)
        {
            Assert.That(response.StatusCode, Is.EqualTo(statusCode), response.Body);
            var message = Message(response);
            Assert.That(message["jsonrpc"]!.GetValue<string>(), Is.EqualTo("2.0"));
            Assert.That(message["result"], Is.Null);
            Assert.That(message["error"]!["code"]!.GetValue<int>(), Is.EqualTo(code));
            Assert.That(message["error"]!["message"]!.GetValue<string>(), Is.Not.Empty);
            if (id is null)
            {
                Assert.That(message["id"], Is.Null);
            }
            else
            {
                Assert.That(message["id"]!.GetValue<int>(), Is.EqualTo(id));
            }
        }

        private sealed class EchoTool : IMcpTool
        {
            public string Name => "echo";

            public string Description => "Echoes value.";

            public JsonElement InputSchema { get; } = McpSchema.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""");

            public ToolAnnotations Annotations => McpToolAnnotations.ReadOnly();

            public Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
                => Task.FromResult(McpToolResult.Success(
                    "Echo.",
                    new { caller = call.Caller.SubjectId },
                    new { value = call.Arguments.RequiredString("value") }));
        }

        private sealed class ThrowingTool : IMcpTool
        {
            public string Name => "explode";

            public string Description => "Always fails.";

            public JsonElement InputSchema { get; } = McpSchema.Parse("""{"type":"object"}""");

            public ToolAnnotations Annotations => McpToolAnnotations.Additive();

            public Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
                => throw new InvalidOperationException("boom");
        }
    }
}
