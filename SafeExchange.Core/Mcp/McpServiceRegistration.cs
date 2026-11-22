/// <summary>
/// McpServiceRegistration
/// </summary>

namespace SafeExchange.Core.Mcp
{
    using Microsoft.Extensions.DependencyInjection;
    using SafeExchange.Core.Mcp.OAuth;
    using SafeExchange.Core.Mcp.Tools;
    using System;
    using System.Net.Http;

    public static class McpServiceRegistration
    {
        /// <summary>
        /// Registers the MCP tools (scoped, like the services they call), their registry, the dispatcher, the
        /// protected-resource facts and the OAuth broker with its Entra token client. Needs the SafeExchange services
        /// and the <c>Authentication</c>, <c>Features</c> and <c>Mcp:OAuth</c> options.
        /// </summary>
        public static IServiceCollection AddSafeExchangeMcp(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddScoped<IMcpTool, WhoAmITool>();
            services.AddScoped<IMcpTool, SecretListTool>();
            services.AddScoped<IMcpTool, SecretGetTool>();
            services.AddScoped<IMcpTool, SecretCreateTool>();
            services.AddScoped<IMcpTool, SecretUpdateTool>();
            services.AddScoped<IMcpTool, SecretDeleteTool>();
            services.AddScoped<IMcpTool, SecretReadTextTool>();
            services.AddScoped<IMcpTool, FileUploadBeginTool>();
            services.AddScoped<IMcpTool, FileUploadChunkTool>();
            services.AddScoped<IMcpTool, FileUploadCompleteTool>();
            services.AddScoped<IMcpTool, FileDownloadChunkTool>();
            services.AddScoped<IMcpTool, FileDeleteTool>();
            services.AddScoped<IMcpTool, FileTransferInfoTool>();
            services.AddScoped<IMcpTool, AccessListTool>();
            services.AddScoped<IMcpTool, AccessChangeTool>();
            services.AddScoped<IMcpTool, AuditReadTool>();

            services.AddScoped<McpToolRegistry>();
            services.AddScoped<McpRequestDispatcher>();
            services.AddSingleton<McpProtectedResource>();

            services.AddHttpClient(EntraTokenClient.HttpClientName, client => client.Timeout = EntraTokenClient.Timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddSingleton<IEntraTokenClient, EntraTokenClient>();
            services.AddSingleton<McpOAuthBroker>();
            return services;
        }
    }
}
