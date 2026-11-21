/// <summary>
/// ICallerResolver
/// </summary>

namespace SafeExchange.Core.Callers
{
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Turns an authenticated principal into a <see cref="Caller"/>, or a denial. Hosts whose tokens
    /// carry different claims register their own implementation after <c>SafeExchangeStartup.ConfigureServices</c>.
    /// </summary>
    public interface ICallerResolver
    {
        Task<CallerResolution> ResolveAsync(ClaimsPrincipal principal, string channel, string? clientLabel, CancellationToken cancellationToken = default);
    }
}
