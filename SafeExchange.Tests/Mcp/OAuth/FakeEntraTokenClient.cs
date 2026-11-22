/// <summary>
/// FakeEntraTokenClient
/// </summary>

namespace SafeExchange.Tests.Mcp.OAuth
{
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Records every form the broker forwards and answers with <see cref="Response"/> (or throws <see cref="Failure"/>),
    /// after <see cref="Hold"/> completes when it is set.
    /// </summary>
    public sealed class FakeEntraTokenClient : IEntraTokenClient
    {
        public const string AccessToken = "eyJ0eXAiOiJKV1QifQ.eyJzdWIiOiJmYWtlLWVudHJhIn0.c2lnbmF0dXJlLWZvci10ZXN0cw";

        public const string RefreshToken = "0.AAAA-fake-entra-refresh-token-for-tests";

        public static readonly string SuccessBody =
            "{\"token_type\":\"Bearer\",\"scope\":\"api://safeexchange.test/user_impersonation\",\"expires_in\":3599,"
            + $"\"access_token\":\"{AccessToken}\",\"refresh_token\":\"{RefreshToken}\"}}";

        public List<Dictionary<string, string>> Requests { get; } = new();

        public EntraTokenResponse Response { get; set; } = new(HttpStatusCode.OK, SuccessBody);

        public Exception? Failure { get; set; }

        public Task? Hold { get; set; }

        public async Task<EntraTokenResponse> RedeemAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(form.ToDictionary(p => p.Key, p => p.Value));
            if (this.Hold is not null)
            {
                await this.Hold;
            }

            if (this.Failure is not null)
            {
                throw this.Failure;
            }

            return this.Response;
        }
    }
}
