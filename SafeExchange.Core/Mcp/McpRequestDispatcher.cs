/// <summary>
/// McpRequestDispatcher
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.Logging;
    using ModelContextProtocol;
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Services;
    using System;
    using System.Diagnostics;
    using System.Net;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Host-agnostic JSON-RPC handling for the stateless MCP endpoint: one message in, one JSON response out.
    /// Requests get a result or a JSON-RPC error; notifications and responses get <c>202</c> without a body.
    /// Never logs message bodies, arguments or results: only tool names, secret names, status and duration.
    /// </summary>
    public sealed class McpRequestDispatcher
    {
        private static readonly JsonSerializerOptions ProtocolJson = McpJsonUtilities.DefaultOptions;

        private static readonly string ServerVersion = typeof(McpRequestDispatcher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        private readonly McpToolRegistry registry;

        private readonly ILogger log;

        public McpRequestDispatcher(McpToolRegistry registry, ILogger<McpRequestDispatcher> log)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<McpResponse> DispatchAsync(McpRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!string.IsNullOrEmpty(request.ProtocolVersion) && !McpProtocol.IsSupported(request.ProtocolVersion))
            {
                return Error(HttpStatusCode.BadRequest, default, McpErrorCode.InvalidRequest, "Unsupported MCP-Protocol-Version.");
            }

            JsonRpcMessage? message;
            try
            {
                using var document = JsonDocument.Parse(request.Body ?? string.Empty);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    return Error(HttpStatusCode.BadRequest, default, McpErrorCode.InvalidRequest, "Batch requests are not supported.");
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return Error(HttpStatusCode.BadRequest, default, McpErrorCode.InvalidRequest, "Invalid request: expected one JSON-RPC message object.");
                }

                try
                {
                    message = JsonSerializer.Deserialize<JsonRpcMessage>(request.Body!, ProtocolJson);
                }
                catch (JsonException)
                {
                    return Error(HttpStatusCode.BadRequest, IdOf(root), McpErrorCode.InvalidRequest, "Invalid request: not a JSON-RPC 2.0 message.");
                }
            }
            catch (JsonException)
            {
                return Error(HttpStatusCode.BadRequest, default, McpErrorCode.ParseError, "Parse error: the body is not valid JSON.");
            }

            return message is JsonRpcRequest rpcRequest
                ? await this.HandleAsync(rpcRequest, request, cancellationToken)
                : McpResponse.Accepted;
        }

        private async Task<McpResponse> HandleAsync(JsonRpcRequest rpcRequest, McpRequest request, CancellationToken cancellationToken)
        {
            switch (rpcRequest.Method)
            {
                case RequestMethods.Initialize:
                    return Initialize(rpcRequest);

                case RequestMethods.Ping:
                    return Result(rpcRequest.Id, new PingResult());

                case RequestMethods.ToolsList:
                    return Result(rpcRequest.Id, this.registry.ToListToolsResult());

                case RequestMethods.ToolsCall:
                    return await this.CallToolAsync(rpcRequest, request, cancellationToken);

                default:
                    return Error(HttpStatusCode.OK, rpcRequest.Id, McpErrorCode.MethodNotFound, "Method not found.");
            }
        }

        private static McpResponse Initialize(JsonRpcRequest rpcRequest)
        {
            var parameters = ParamsOf<InitializeRequestParams>(rpcRequest);
            if (parameters is null)
            {
                return Error(HttpStatusCode.OK, rpcRequest.Id, McpErrorCode.InvalidParams, "Invalid params: initialize needs protocolVersion, capabilities and clientInfo.");
            }

            var result = new InitializeResult
            {
                ProtocolVersion = McpProtocol.Negotiate(parameters.ProtocolVersion),
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ServerInfo = new Implementation { Name = McpProtocol.ServerName, Title = McpProtocol.ServerTitle, Version = ServerVersion },
                Instructions = McpProtocol.Instructions,
            };

            return Result(rpcRequest.Id, result) with { SessionId = McpClientLabel.ToSessionId(parameters.ClientInfo) };
        }

        private async Task<McpResponse> CallToolAsync(JsonRpcRequest rpcRequest, McpRequest request, CancellationToken cancellationToken)
        {
            var parameters = ParamsOf<CallToolRequestParams>(rpcRequest);
            if (parameters is null || string.IsNullOrEmpty(parameters.Name))
            {
                return Error(HttpStatusCode.OK, rpcRequest.Id, McpErrorCode.InvalidParams, "Invalid params: tools/call needs a tool name.");
            }

            if (!this.registry.TryGet(parameters.Name, out var tool))
            {
                return Error(HttpStatusCode.OK, rpcRequest.Id, McpErrorCode.InvalidParams, $"Unknown tool: '{parameters.Name}'.");
            }

            var arguments = rpcRequest.Params?["arguments"] is JsonObject argumentsObject
                ? new McpArguments(JsonSerializer.SerializeToElement(argumentsObject))
                : McpArguments.Empty;
            var result = await this.RunToolAsync(tool, new McpToolCall(request.Caller, arguments, request.ApiBaseUri), cancellationToken);
            return Result(rpcRequest.Id, result.ToCallToolResult());
        }

        private async Task<McpToolResult> RunToolAsync(IMcpTool tool, McpToolCall call, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            McpToolResult result;
            try
            {
                result = await tool.ExecuteAsync(call, cancellationToken);
            }
            catch (McpArgumentException exception)
            {
                result = McpToolResult.Error(McpToolResult.InvalidArgumentsStatus, exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var correlationId = Guid.NewGuid().ToString("N");
                var logged = exception is ServiceOperationException ? exception.InnerException ?? exception : exception;
                this.log.LogError(logged, "MCP tool {ToolName} failed, correlationId={CorrelationId}", tool.Name, correlationId);
                result = McpToolResult.Error(ServiceStatus.Error.ToStatusString(), $"Internal error. Reference: {correlationId}");
            }

            this.log.LogInformation(
                "MCP tool {ToolName} on secret '{SecretName}' finished with {Status} in {ElapsedMs} ms.",
                tool.Name, SecretNameOf(call.Arguments), StatusOf(result), stopwatch.ElapsedMilliseconds);
            return result;
        }

        private static string StatusOf(McpToolResult result)
            => result.IsError ? result.StructuredContent["status"]?.GetValue<string>() ?? "error" : ServiceStatus.Ok.ToStatusString();

        private static string SecretNameOf(McpArguments arguments)
        {
            try
            {
                return arguments.OptionalString("name") ?? string.Empty;
            }
            catch (McpArgumentException)
            {
                return string.Empty;
            }
        }

        private static T? ParamsOf<T>(JsonRpcRequest rpcRequest) where T : class
        {
            if (rpcRequest.Params is not JsonObject parameters)
            {
                return null;
            }

            try
            {
                return parameters.Deserialize<T>(ProtocolJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static RequestId IdOf(JsonElement message)
        {
            if (!message.TryGetProperty("id", out var id))
            {
                return default;
            }

            return id.ValueKind switch
            {
                JsonValueKind.String => new RequestId(id.GetString()!),
                JsonValueKind.Number when id.TryGetInt64(out var number) => new RequestId(number),
                _ => default,
            };
        }

        private static McpResponse Result<T>(RequestId id, T result)
        {
            var response = new JsonRpcResponse { Id = id, Result = JsonSerializer.SerializeToNode(result, ProtocolJson) ?? new JsonObject() };
            return new McpResponse(HttpStatusCode.OK, JsonSerializer.Serialize<JsonRpcMessage>(response, ProtocolJson));
        }

        private static McpResponse Error(HttpStatusCode statusCode, RequestId id, McpErrorCode code, string message)
        {
            var error = new JsonRpcError { Id = id, Error = new JsonRpcErrorDetail { Code = (int)code, Message = message } };
            return new McpResponse(statusCode, JsonSerializer.Serialize<JsonRpcMessage>(error, ProtocolJson));
        }
    }
}
