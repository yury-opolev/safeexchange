/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Graph;
    using System.Threading.Tasks;

    public interface IGraphClientProvider
    {
        Task<GraphServiceClient> GetGraphClientAsync(TokenResult tokenResult, string[] scopes, ILogger logger);
    }
}