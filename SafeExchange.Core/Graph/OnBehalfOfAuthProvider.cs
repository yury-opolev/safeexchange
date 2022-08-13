/// <summary>
/// OnBehalfOfAuthProvider
/// </summary>

namespace SafeExchange.Core.Graph
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using Microsoft.Identity.Client;
    using System;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;

    public class OnBehalfOfAuthProvider : IAuthenticationProvider
    {
        private IConfidentialClientApplication msalClient;

        private AccountIdAndToken accountIdAndToken;

        private string[] scopes;

        private ILogger logger;

        private OnBehalfOfTokenProviderResult lastResult;

        public OnBehalfOfAuthProvider(IConfidentialClientApplication msalClient, AccountIdAndToken accountIdAndToken, string[] scopes, ILogger logger)
        {
            this.scopes = scopes;
            this.logger = logger;

            this.accountIdAndToken = accountIdAndToken;
            this.msalClient = msalClient;
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

        public async Task AuthenticateRequestAsync(HttpRequestMessage requestMessage)
        {
            if (!lastResult.Success)
            {
                throw new InvalidOperationException("Cannot authenticate without access token.");
            }

            var accessTokenResult = await this.TryGetAccessTokenAsync();
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenResult.Token);
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
                var consentRequired = msalUiRequiredException.ErrorCode.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase);
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
                var consentRequired = msalUiRequiredException.ErrorCode.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase);
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
