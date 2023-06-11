

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.Azure.Functions.Worker.Http;
    using Microsoft.Azure.Functions.Worker;
    using System.IO;
    using System.Net;
    using System.Text.Json;

    public class TestHttpResponseData : HttpResponseData
    {
        public TestHttpResponseData(FunctionContext functionContext)
            : base(functionContext)
        {
        }

        public override HttpStatusCode StatusCode { get; set; }

        public override HttpHeadersCollection Headers { get; set; } = new HttpHeadersCollection();

        public override Stream Body { get; set; } = new MemoryStream();

        public override HttpCookies Cookies { get; }

        public T? ReadBodyAsJson<T>()
        {
            this.Body.Seek(0, SeekOrigin.Begin);
            return JsonSerializer.Deserialize<T>(this.Body ?? new MemoryStream());
        }
    }
}
