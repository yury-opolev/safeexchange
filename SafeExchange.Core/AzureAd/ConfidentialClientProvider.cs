/// <summary>
/// ConfidentialClientProvider
/// </summary>

namespace SafeExchange.Core.AzureAd
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Identity.Client;
    using System;

    public class ConfidentialClientProvider : IConfidentialClientProvider
    {
        private readonly IConfiguration configuration;

        private IConfidentialClientApplication? client;

        private object locker = new object();

        public ConfidentialClientProvider(IConfiguration configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <inheritdoc />
        public IConfidentialClientApplication GetConfidentialClient()
        {
            return this.GetOrCreateClient();
        }

        private IConfidentialClientApplication GetOrCreateClient()
        {
            if (this.client != null)
            {
                return this.client;
            }

            lock (this.locker)
            {
                if (this.client != null)
                {
                    return this.client;
                }

                var aadClientSettings = this.configuration.GetSection("AADClient");
                this.client = ConfidentialClientApplicationBuilder
                    .Create(aadClientSettings["ClientId"])
                    .WithAuthority(AadAuthorityAudience.AzureAdMyOrg, true)
                    .WithTenantId(aadClientSettings["TenantId"])
                    .WithClientSecret(aadClientSettings["ClientSecret"])
                    .Build();
            }

            return this.client;
        }
    }
}
