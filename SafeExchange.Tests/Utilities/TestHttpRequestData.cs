/// <summary>
/// TestHttpRequestData
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.IdentityModel.Tokens;
    using SafeExchange.Core;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Claims;
    using System.Text;
    using System.Text.Json;

    public class TestHttpRequestData : HttpRequestData
    {
        public TestHttpRequestData(FunctionContext functionContext)
            : base(functionContext) { }

        public override Stream Body { get => this.body; }

        private Stream body;

        public override HttpHeadersCollection Headers { get; } = new HttpHeadersCollection();

        public override IReadOnlyCollection<IHttpCookie> Cookies { get; }

        public override Uri Url { get; }

        public override IEnumerable<CaseSensitiveClaimsIdentity> Identities { get => this.identities; }

        private ICollection<CaseSensitiveClaimsIdentity> identities;

        public override string Method { get => this.method; }

        private string method;

        public override HttpResponseData CreateResponse()
        {
            return new TestHttpResponseData(FunctionContext);
        }

        public void SetIdentities(ICollection<CaseSensitiveClaimsIdentity> identitiesToSet)
        {
            this.identities = identitiesToSet;
        }

        public void SetMethod(string methodToSet)
        {
            this.method = methodToSet;
        }

        public void SetBodyAsJson<T>(T content)
        {
            var serializedContent = DefaultJsonSerializer.Serialize(content);
            var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(serializedContent));
            this.SetBodyAsStream(contentStream);
        }

        public void SetBodyAsStream(Stream bodyToSet)
        {
            this.body = bodyToSet;
        }
    }
}
