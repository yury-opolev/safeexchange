/// <summary>
/// OnBehalfOfAuthProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Identity.Client;
    using Microsoft.Kiota.Abstractions.Authentication;
    using System;
    using System.Threading.Tasks;

    public class OnBehalfOfAuthProvider : IAccessTokenProvider
    {
        private IConfidentialClientApplication msalClient;

        private AccountIdAndToken accountIdAndToken;

        private string[] scopes;

        private ILogger logger;

        private OnBehalfOfTokenProviderResult lastResult;

        public AllowedHostsValidator AllowedHostsValidator => new();

        public OnBehalfOfAuthProvider(IConfidentialClientApplication msalClient, AccountIdAndToken accountIdAndToken, string[] scopes, ILogger logger)
        {
            this.scopes = scopes;
            this.logger = logger;

            this.accountIdAndToken = accountIdAndToken;
            this.msalClient = msalClient;
        }

        public async Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
        {
            if (!lastResult.Success)
            {
                throw new InvalidOperationException("Could not get authorization token.");
            }

            var accessTokenResult = await this.TryGetAccessTokenAsync();
            return accessTokenResult.Token;
        }

        public async Task<OnBehalfOfTokenProviderResult> TryGetAccessTokenAsync()
        {
            if (lastResult?.Success == true && lastResult?.ExpiresOn > (DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)))
            {
                return lastResult;
            }

            var account = await this.msalClient.GetAccountAsync(this.accountIdAndToken.AccountId);
            if (account != null)
            {
                lastResult = await this.GetTokenSilentlyAsync(account);
            }
            else
            {
                lastResult = await this.GetOnBehalfTokenAsync();
            }

            return lastResult;
        }

        private async Task<OnBehalfOfTokenProviderResult> GetTokenSilentlyAsync(IAccount account)
        {
            try
            {
                var cacheResult = await this.msalClient
                    .AcquireTokenSilent(this.scopes, account).ExecuteAsync();

                this.logger.LogInformation($"Token for [{string.Join(',', this.scopes)}] on behalf of '{this.accountIdAndToken.AccountId}' acquired silently.");
                return new OnBehalfOfTokenProviderResult()
                {
                    Success = true,
                    Token = cacheResult.AccessToken,
                    ExpiresOn = cacheResult.ExpiresOn
                };
            }
            catch (MsalUiRequiredException msalUiRequiredException)
            {
                this.logger.LogInformation($"Cannot acquire token for [{string.Join(',', this.scopes)}] silently, UI required. Error code: {msalUiRequiredException.ErrorCode}.");
                var consentRequired = msalUiRequiredException.Classification == UiRequiredExceptionClassification.ConsentRequired;
                return new OnBehalfOfTokenProviderResult() { ConsentRequired = consentRequired };
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, $"{exception.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, silently.");
            }

            return new OnBehalfOfTokenProviderResult();
        }

        private async Task<OnBehalfOfTokenProviderResult> GetOnBehalfTokenAsync()
        {
            try
            {
                var userAssertion = new UserAssertion(this.accountIdAndToken.Token);

                var result = await this.msalClient
                    .AcquireTokenOnBehalfOf(this.scopes, userAssertion).ExecuteAsync();

                this.logger.LogInformation($"Acquired on-behalf of '{this.accountIdAndToken.AccountId}' access token for: [{string.Join(',', this.scopes)}]");
                return new OnBehalfOfTokenProviderResult()
                {
                    Success = true,
                    Token = result.AccessToken,
                    ExpiresOn = result.ExpiresOn
                };
            }
            catch (MsalUiRequiredException msalUiRequiredException)
            {
                this.logger.LogError(msalUiRequiredException, $"{msalUiRequiredException.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, on-behalf, UI required. Error code: {msalUiRequiredException.ErrorCode}.");
                var consentRequired = msalUiRequiredException.Classification == UiRequiredExceptionClassification.ConsentRequired;
                return new OnBehalfOfTokenProviderResult() { ConsentRequired = consentRequired };
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, $"{exception.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, on-behalf.");
            }

            return new OnBehalfOfTokenProviderResult();
        }
    }
}
