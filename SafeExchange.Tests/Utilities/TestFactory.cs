/// <summary>
/// TestFactory
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Internal;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.Extensions.Primitives;
    using System.Collections.Generic;

    public class TestFactory
    {
        public static HttpRequest CreateHttpRequest(string requestMethod, Dictionary<string, StringValues> queryValues = null)
        {
            var context = new DefaultHttpContext();

            var request = context.Request;
            request.Method = requestMethod;

            if (queryValues != null)
            {
                request.Query = new QueryCollection(queryValues);
            }

            return request;
        }

        public static ILogger CreateLogger(LoggerTypes type = LoggerTypes.Null)
        {
            switch (type)
            {
                case LoggerTypes.Console:
                    return new ConsoleLogger();

                default:
                    return NullLoggerFactory.Instance.CreateLogger("Null Logger");
            }
        }

        public static ILogger<T> CreateLogger<T>(LoggerTypes type = LoggerTypes.Null)
        {
            switch (type)
            {
                case LoggerTypes.Console:
                    return new ConsoleLogger<T>();

                default:
                    return NullLoggerFactory.Instance.CreateLogger<T>();
            }
        }
    }
}
