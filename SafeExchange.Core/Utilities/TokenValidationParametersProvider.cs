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

            this.tokenValidationParameters = new TokenValidationParameters()
            {
                RequireExpirationTime = true,
                RequireSignedTokens = true,

                ValidateAudience = authConfig.ValidateAudience,
                ValidateIssuer = authConfig.ValidateIssuer,
            };

            this.tokenValidationParameters.EnableAadSigningKeyIssuerValidation();

            if (authConfig.ValidateAudience)
            {
                this.tokenValidationParameters.ValidAudiences = authConfig.ValidAudiences.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }

            if (authConfig.ValidateIssuer)
            {
                this.tokenValidationParameters.ValidIssuers = authConfig.ValidIssuers.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }

            this.openIdConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(authConfig.MetadataAddress, new OpenIdConnectConfigurationRetriever());
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
