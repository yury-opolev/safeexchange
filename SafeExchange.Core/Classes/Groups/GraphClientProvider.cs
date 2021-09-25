/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Identity.Client;
    using Microsoft.Graph;
    using System;
    using System.Threading.Tasks;

    public class GraphClientProvider : IGraphClientProvider
    {
        private IConfidentialClientApplication msalClient;

        public GraphClientProvider()
        {
            // ...
        }

        public async Task<GraphServiceClient> GetGraphClientAsync(TokenResult tokenResult, string[] scopes, ILogger logger)
        {
            await this.InitializeMsalClient(logger);

            var authProvider = new OnBehalfOfAuthProvider(this.msalClient, tokenResult, scopes, logger);
            return new GraphServiceClient(authProvider);
        }

        private async Task InitializeMsalClient(ILogger logger)
        {
            if (this.msalClient != null)
            {
                return;
            }

            var systemSettings = new KeyVaultSystemSettings(logger);
            var tokenProviderSettings = await systemSettings.GetTokenProviderSettingsAsync();

            if (tokenProviderSettings == default(TokenProviderSettings))
            {
                throw new ArgumentNullException(nameof(tokenProviderSettings));
            }

            this.msalClient = ConfidentialClientApplicationBuilder
                .Create(tokenProviderSettings.ClientId)
                .WithAuthority(AadAuthorityAudience.AzureAdMyOrg, true)
                .WithTenantId(tokenProviderSettings.TenantId)
                .WithClientSecret(tokenProviderSettings.ClientSecret)
                .Build();
        }

    }
}