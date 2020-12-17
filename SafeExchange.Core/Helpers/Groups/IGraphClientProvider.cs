/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;

    public interface IGraphClientProvider
    {
        GraphServiceClient GetGraphClient(TokenResult tokenResult, string[] scopes, ILogger logger);
    }
}