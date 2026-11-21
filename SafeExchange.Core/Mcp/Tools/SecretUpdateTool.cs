/// <summary>
/// SecretUpdateTool
/// </summary>

namespace SafeExchange.Core.Mcp.Tools
{
    using ModelContextProtocol.Protocol;
    using SafeExchange.Core.Model.Dto.Input;
    using SafeExchange.Core.Services;
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class SecretUpdateTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpSchema.Parse($$$"""
            {"type":"object","properties":{"name":{{{McpSchema.SecretName}}},"expirationSettings":{{{McpSchema.ExpirationSettings}}},"tags":{{{McpSchema.Tags}}}},"required":["name","expirationSettings"]}
            """);

        private readonly SecretService secrets;

        public SecretUpdateTool(SecretService secrets)
        {
            this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        }

        public string Name => "secret_update";

        public string Description => "Replaces a secret's expiration settings and, when tags is given, its tags (needs Write).";

        public JsonElement InputSchema => Schema;

        public ToolAnnotations Annotations => McpToolAnnotations.Additive();

        public async Task<McpToolResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken)
        {
            var name = call.Arguments.RequiredString("name");
            var input = call.Arguments.Bind<MetadataUpdateInput>();

            var result = await this.secrets.UpdateAsync(call.Caller, name, input);
            return result.IsOk
                ? McpSecretView.Success($"Updated secret '{name}'.", result.Value)
                : McpToolResult.Failure(result.Outcome);
        }
    }
}
