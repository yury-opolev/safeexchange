/// <summary>
/// ConfidentialClientProvider
/// </summary>

namespace SafeExchange.Core.AzureAd
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Identity.Client;
    using System;
    using System.Configuration;
    using System.Security.Cryptography.X509Certificates;

    public class ConfidentialClientProvider : IConfidentialClientProvider
    {
        private readonly IConfiguration configuration;

        private readonly ILogger<ConfidentialClientProvider> log;

        private IConfidentialClientApplication? client;

        private object locker = new object();

        public ConfidentialClientProvider(IConfiguration configuration, ILogger<ConfidentialClientProvider> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
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

                var clientId = aadClientSettings["ClientId"];
                var clientSecret = aadClientSettings["ClientSecret"];
                var clientBuilder = ConfidentialClientApplicationBuilder
                    .Create(clientId)
                    .WithAuthority(AadAuthorityAudience.AzureAdMyOrg, true)
                    .WithTenantId(aadClientSettings["TenantId"]);

                if (string.IsNullOrEmpty(clientSecret))
                {
                    this.log.LogInformation($"{nameof(ConfidentialClientProvider)} is creating Microsoft Entra ID client '{clientId}' with specified certificate.");
                    var clientCertificate = this.GetClientCertificate();

                    var sendX5CSetting = aadClientSettings["SendX5C"];
                    if (string.IsNullOrEmpty(sendX5CSetting) || !bool.TryParse(aadClientSettings["SendX5C"], out var sendX5C))
                    {
                        sendX5C = false;
                    }

                    clientBuilder.WithCertificate(clientCertificate, sendX5C);
                }
                else
                {
                    this.log.LogInformation($"{nameof(ConfidentialClientProvider)} is creating Microsoft Entra ID client '{clientId}' with specified secret.");
                    clientBuilder.WithClientSecret(clientSecret);
                }

                this.client = clientBuilder.Build();
            }

            return this.client;
        }

        private X509Certificate2 GetClientCertificate()
        {
            var aadClientSettings = this.configuration.GetSection("AADClient");
            var certificateBytesBase64 = aadClientSettings["ClientCertificate"];
            if (string.IsNullOrEmpty(certificateBytesBase64))
            {
                throw new ConfigurationErrorsException("Could not retrieve Microsoft Entra ID client certificate.");
            }

            byte[] certBytes = Convert.FromBase64String(certificateBytesBase64);
            return new X509Certificate2(certBytes);
        }
    }
}
