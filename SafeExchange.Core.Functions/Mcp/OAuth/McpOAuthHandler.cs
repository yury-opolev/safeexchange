/// <summary>
/// McpOAuthHandler
/// </summary>

namespace SafeExchange.Core.Functions.Mcp.OAuth
{
    using Microsoft.Azure.Functions.Worker.Http;
    using SafeExchange.Core.Mcp.OAuth;
    using System;
    using System.Globalization;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Functions adapters for the anonymous OAuth broker routes: each request is first checked against
    /// <see cref="McpOAuthThrottle"/> (client address = last <c>X-Forwarded-For</c> entry), then the request URL, the
    /// UTF-8 body and the consent cookie go into <see cref="McpOAuthBroker"/>, and its result goes out as status,
    /// <c>Location</c>, <c>Set-Cookie</c>, <c>Retry-After</c>, a JSON or HTML body (HTML with
    /// <see cref="ConsentPage.SecurityHeaders"/>) and <c>Cache-Control: no-store</c>. A disabled broker answers a bare 404.
    /// </summary>
    public sealed class McpOAuthHandler
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly McpOAuthBroker broker;

        private readonly McpOAuthThrottle throttle;

        public McpOAuthHandler(McpOAuthBroker broker, McpOAuthThrottle throttle)
        {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
            this.throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        }

        public Task<HttpResponseData> Metadata(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return WriteAsync(request, this.broker.Metadata(McpHandler.ApiBaseUri(request.Url)));
        }

        public async Task<HttpResponseData> Register(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = this.Throttle(request, McpOAuthThrottle.Register)
                ?? this.broker.Register(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request));
            return await WriteAsync(request, result);
        }

        /// <summary>
        /// <c>GET</c> answers the consent page; <c>POST</c> is the consent form.
        /// </summary>
        public async Task<HttpResponseData> Authorize(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var apiBaseUri = McpHandler.ApiBaseUri(request.Url);
            var result = this.Throttle(request, McpOAuthThrottle.Authorize)
                ?? (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                    ? this.broker.AuthorizeConsent(apiBaseUri, await ReadBodyAsync(request), ReadConsentCookie(request))
                    : this.broker.Authorize(apiBaseUri, request.Url.Query));
            return await WriteAsync(request, result);
        }

        public Task<HttpResponseData> Callback(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = this.Throttle(request, McpOAuthThrottle.Callback)
                ?? this.broker.Callback(McpHandler.ApiBaseUri(request.Url), request.Url.Query, ReadConsentCookie(request));
            return WriteAsync(request, result);
        }

        public async Task<HttpResponseData> Token(HttpRequestData request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var result = this.Throttle(request, McpOAuthThrottle.Token)
                ?? await this.broker.TokenAsync(McpHandler.ApiBaseUri(request.Url), await ReadBodyAsync(request), request.FunctionContext.CancellationToken);
            return await WriteAsync(request, result);
        }

        // A disabled broker is not throttled: it answers 404 to everything.
        private McpOAuthResult? Throttle(HttpRequestData request, string endpoint)
            => this.broker.IsEnabled ? this.throttle.Check(endpoint, ReadClientAddress(request)) : null;

        private static string? ReadClientAddress(HttpRequestData request)
            => request.Headers.TryGetValues("X-Forwarded-For", out var values) ? ClientAddress.FromForwardedFor(string.Join(",", values)) : null;

        // HTTP/2 may split Cookie into several header fields; they are joined with "; " (RFC 9113 §8.2.3).
        private static string? ReadConsentCookie(HttpRequestData request)
            => request.Headers.TryGetValues("Cookie", out var values) ? ConsentCookie.Read(string.Join("; ", values)) : null;

        // Reads one character more than the broker accepts, so an oversize body is refused without buffering all of it.
        // A body that is not valid UTF-8 reads as null, which the broker refuses like an empty one.
        private static async Task<string?> ReadBodyAsync(HttpRequestData request)
        {
            using var reader = new StreamReader(request.Body ?? Stream.Null, StrictUtf8);
            var buffer = new char[McpOAuthBroker.MaxBodyLength + 1];
            var length = 0;
            int read;
            try
            {
                while (length < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(length))) > 0)
                {
                    length += read;
                }
            }
            catch (DecoderFallbackException)
            {
                return null;
            }

            return new string(buffer, 0, length);
        }

        private static async Task<HttpResponseData> WriteAsync(HttpRequestData request, McpOAuthResult result)
        {
            var response = request.CreateResponse(result.StatusCode);
            if (result == McpOAuthResult.NotFound)
            {
                return response;
            }

            response.Headers.Add("Cache-Control", "no-store");
            if (result.Location is not null)
            {
                response.Headers.Add("Location", result.Location);
            }

            if (result.Cookie is not null)
            {
                response.Headers.Add("Set-Cookie", result.Cookie.ToSetCookieHeader());
            }

            if (result.RetryAfterSeconds is int retryAfter)
            {
                response.Headers.Add("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
            }

            if (result.Body is not null)
            {
                response.Headers.Add("Content-Type", result.ContentType);
                if (result.IsHtml)
                {
                    foreach (var header in ConsentPage.SecurityHeaders)
                    {
                        response.Headers.Add(header.Key, header.Value);
                    }
                }

                await response.WriteStringAsync(result.Body);
            }

            return response;
        }
    }
}
