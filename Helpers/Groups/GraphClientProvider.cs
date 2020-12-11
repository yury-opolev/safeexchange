/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Identity.Client;
    using Microsoft.Graph;
    using System;

    public class GraphClientProvider : IGraphClientProvider
    {
        private IConfidentialClientApplication msalClient;

        public GraphClientProvider()
        {
            // ...
        }

        public GraphServiceClient GetGraphClient(TokenResult tokenResult, string[] scopes, ILogger logger)
        {
            this.InitializeMsalClient();

            var authProvider = new OnBehalfOfAuthProvider(this.msalClient, tokenResult, scopes, logger);
            return new GraphServiceClient(authProvider);
        }

        private void InitializeMsalClient()
        {
            if (this.msalClient != null)
            {
                return;
            }

            var aadClientId = Environment.GetEnvironmentVariable("TOKENPROVIDER-AAD-CLIENT-ID");
            if (string.IsNullOrEmpty(aadClientId))
            {
                throw new ArgumentNullException("TOKENPROVIDER-AAD-CLIENT-ID");
            }
  
            var tenantId = Environment.GetEnvironmentVariable("TOKENPROVIDER-TENANT-ID");
            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException("TOKENPROVIDER-TENANT-ID");
            }

            var aadClientSecret = Environment.GetEnvironmentVariable("TOKENPROVIDER-AAD-CLIENT-SECRET");
            if (string.IsNullOrEmpty(aadClientSecret))
            {
                throw new ArgumentNullException("TOKENPROVIDER-AAD-CLIENT-SECRET");
            }

            this.msalClient = ConfidentialClientApplicationBuilder
                .Create(aadClientId)
                .WithAuthority(AadAuthorityAudience.AzureAdMyOrg, true)
                .WithTenantId(tenantId)
                .WithClientSecret(aadClientSecret)
                .Build();
        }

    }
}