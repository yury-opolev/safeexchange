/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;

    public interface IGraphClientProvider
    {
        GraphServiceClient GetGraphClient(TokenResult tokenResult, string[] scopes, ILogger logger);
    }
}