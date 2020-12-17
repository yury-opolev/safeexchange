/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using Microsoft.Identity.Client;

    public class OnBehalfOfAuthProvider : IAuthenticationProvider
    {
        private IConfidentialClientApplication msalClient;

        private TokenResult tokenResult;

        private string[] scopes;

        private ILogger logger;

        public OnBehalfOfAuthProvider(IConfidentialClientApplication msalClient, TokenResult tokenResult, string[] scopes, ILogger logger)
        {
            this.scopes = scopes;
            this.logger = logger;

            this.tokenResult = tokenResult;
            this.msalClient = msalClient;
        }

        public async Task<string> GetAccessToken()
        {
            try
            {
                var account = await this.msalClient.GetAccountAsync(this.tokenResult.AccountId);
                if (account != null)
                {
                    var cacheResult = await this.msalClient
                        .AcquireTokenSilent(this.scopes, account)
                        .ExecuteAsync();

                    this.logger.LogInformation($"User access token for [{string.Join(',', this.scopes)}] acquired silently");
                    return cacheResult.AccessToken;
                }
            }
            catch (MsalUiRequiredException)
            {
                this.logger.LogInformation($"Cannot acquire token for [{string.Join(',', this.scopes)}] silently");
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, $"{exception.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, silently.");
                return null;
            }

            try
            {
                var userAssertion = new UserAssertion(this.tokenResult.Token);

                var result = await this.msalClient
                    .AcquireTokenOnBehalfOf(this.scopes, userAssertion)
                    .ExecuteAsync();

                this.logger.LogInformation($"Acquired on-behalf user access token for: [{string.Join(',', this.scopes)}]");
                return result.AccessToken;
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, $"{exception.GetType()} getting access token in {nameof(OnBehalfOfAuthProvider)}, on-behalf.");
                return null;
            }
        }

        public async Task AuthenticateRequestAsync(HttpRequestMessage requestMessage)
        {
            var token = await GetAccessToken();
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}