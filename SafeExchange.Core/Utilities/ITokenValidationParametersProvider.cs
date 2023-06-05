/// <summary>
/// ITokenValidationParametersProvider
/// </summary>

namespace SafeExchange.Core
{
    using Microsoft.IdentityModel.Tokens;

    public interface ITokenValidationParametersProvider
    {
        public Task<TokenValidationParameters> GetTokenValidationParametersAsync();
    }
}
