/// <summary>
/// IEntraTokenClient
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Posts a token request form to Entra's fixed token endpoint and returns Entra's status and body unchanged.
    /// </summary>
    public interface IEntraTokenClient
    {
        Task<EntraTokenResponse> RedeemAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default);
    }

    public sealed record EntraTokenResponse(HttpStatusCode StatusCode, string Body);
}
