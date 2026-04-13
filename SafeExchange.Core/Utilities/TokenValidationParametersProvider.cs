/// <summary>
/// TokenValidationParametersProvider
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.IdentityModel.Protocols.OpenIdConnect;
    using Microsoft.IdentityModel.Protocols;
    using Microsoft.IdentityModel.Tokens;
    using SafeExchange.Core.Configuration;
    using System.Configuration;
    using Microsoft.Extensions.Logging;
    using System.Text;
    using Microsoft.IdentityModel.Validators;

    public class TokenValidationParametersProvider : ITokenValidationParametersProvider
    {
        private readonly TokenValidationParameters tokenValidationParameters;

        private readonly ConfigurationManager<OpenIdConnectConfiguration> openIdConfigurationManager;

        private readonly ILogger log;

        public TokenValidationParametersProvider(IConfiguration configuration, ILogger<TokenValidationParametersProvider> log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            var authConfig = new AuthenticationConfiguration();
            configuration.GetSection("Authentication").Bind(authConfig);

            // Fail loudly at startup rather than silently accept every signed AAD token.
            // With ValidateAudience / ValidateIssuer off, any JWT signed by the AAD
            // common signing keys would authenticate — i.e. any user in any tenant
            // could call this API (OWASP A02:2025 — CWE-1188, CWE-347).
            AssertNotEmpty(authConfig.MetadataAddress, nameof(AuthenticationConfiguration.MetadataAddress));

            if (!authConfig.ValidateAudience)
            {
                throw new ConfigurationErrorsException(
                    "Authentication:ValidateAudience is false. Refusing to start — set it to true and populate Authentication:ValidAudiences.");
            }

            AssertNotEmpty(authConfig.ValidAudiences, nameof(AuthenticationConfiguration.ValidAudiences));

            if (!authConfig.ValidateIssuer)
            {
                throw new ConfigurationErrorsException(
                    "Authentication:ValidateIssuer is false. Refusing to start — set it to true and populate Authentication:ValidIssuers.");
            }

            AssertNotEmpty(authConfig.ValidIssuers, nameof(AuthenticationConfiguration.ValidIssuers));

            var validAudiences = authConfig.ValidAudiences.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var validIssuers = authConfig.ValidIssuers.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (validAudiences.Length == 0)
            {
                throw new ConfigurationErrorsException(
                    "Authentication:ValidAudiences contains no non-empty entries after splitting on ','.");
            }

            if (validIssuers.Length == 0)
            {
                throw new ConfigurationErrorsException(
                    "Authentication:ValidIssuers contains no non-empty entries after splitting on ','.");
            }

            this.tokenValidationParameters = new TokenValidationParameters()
            {
                RequireExpirationTime    = true,
                RequireSignedTokens      = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime         = true,
                ValidateAudience         = true,
                ValidateIssuer           = true,
                ValidAudiences           = validAudiences,
                ValidIssuers             = validIssuers,

                // Algorithm allowlist — do not accept any algorithm the library may
                // default to. Explicitly list the RSA family used by AAD-signed tokens.
                // If AAD ever rotates to a new family the list will need an update —
                // we want that to be a deliberate decision, not a silent acceptance.
                ValidAlgorithms = new[]
                {
                    SecurityAlgorithms.RsaSha256,
                    SecurityAlgorithms.RsaSha384,
                    SecurityAlgorithms.RsaSha512,
                },
            };

            this.tokenValidationParameters.EnableAadSigningKeyIssuerValidation();

            this.openIdConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                authConfig.MetadataAddress, new OpenIdConnectConfigurationRetriever());
        }

        private static void AssertNotEmpty(string value, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationErrorsException(
                    $"Authentication:{propertyName} is empty. Refusing to start.");
            }
        }

        public async Task<TokenValidationParameters> GetTokenValidationParametersAsync()
        {
            this.tokenValidationParameters.IssuerSigningKeys = await this.GetIssuerSigningKeysAsync();
            return this.tokenValidationParameters;
        }

        private async Task<IEnumerable<SecurityKey>> GetIssuerSigningKeysAsync()
        {
            try
            {
                var configuration = await this.openIdConfigurationManager.GetConfigurationAsync(CancellationToken.None);
                return configuration.SigningKeys;
            }
            catch (Exception exception)
            {
                this.log.LogError(GetChainedMessages(exception));
                throw new ConfigurationErrorsException("Cannot get signing keys from OpenID Connect provider.");
            }
        }

        private static string GetChainedMessages(Exception exception)
        {
            StringBuilder result = new StringBuilder($"{exception.GetType()}: {exception.Message}");
            while (exception.InnerException != null)
            {
                exception = exception.InnerException;
                result.Append($" -> {exception.GetType()}: {exception.Message}");
            }

            return result.ToString();
        }
    }
}
