﻿/// <summary>
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

        private DateTime recreateClientAt;

        public bool ShouldRefresh => DateTimeProvider.UtcNow >= this.recreateClientAt;

        public ConfidentialClientProvider(IConfiguration configuration, ILogger<ConfidentialClientProvider> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.recreateClientAt = DateTimeProvider.UtcNow;
        }

        /// <inheritdoc />
        public IConfidentialClientApplication GetConfidentialClient()
        {
            return this.GetOrCreateClient();
        }

        private IConfidentialClientApplication GetOrCreateClient()
        {
            if (this.client != null && !this.ShouldRefresh)
            {
                return this.client;
            }

            lock (this.locker)
            {
                if (this.client != null && !this.ShouldRefresh)
                {
                    return this.client;
                }

                this.log.LogInformation($"{nameof(ConfidentialClientProvider)} - creating Microsoft Entra ID client.");

                var aadClientSettings = this.configuration.GetSection("AADClient");

                var clientId = aadClientSettings["ClientId"];
                var clientSecret = aadClientSettings["ClientSecret"];
                var clientBuilder = ConfidentialClientApplicationBuilder
                    .Create(clientId)
                    .WithAuthority(AadAuthorityAudience.AzureAdMyOrg, true)
                    .WithTenantId(aadClientSettings["TenantId"]);

                if (string.IsNullOrEmpty(clientSecret))
                {
                    var clientCertificate = this.GetClientCertificate();
                    if (!bool.TryParse(aadClientSettings["SendX5C"], out var sendX5C))
                    {
                        sendX5C = false;
                    }

                    this.log.LogInformation($"{nameof(ConfidentialClientProvider)} is creating Microsoft Entra ID client '{clientId}' with specified certificate (effective: {clientCertificate.GetEffectiveDateString()}, expiration date: {clientCertificate.GetExpirationDateString()}, thumbprint: {clientCertificate.Thumbprint}), with sendX5C='{sendX5C}'.");
                    clientBuilder.WithCertificate(clientCertificate, sendX5C);
                }
                else
                {
                    this.log.LogInformation($"{nameof(ConfidentialClientProvider)} is creating Microsoft Entra ID client '{clientId}' with specified secret.");
                    clientBuilder.WithClientSecret(clientSecret);
                }

                this.client = clientBuilder.Build();
                this.recreateClientAt = DateTimeProvider.UtcNow + TimeSpan.FromDays(1);
                this.log.LogInformation($"{nameof(ConfidentialClientProvider)} - next refresh is scheduled at {this.recreateClientAt}.");
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
