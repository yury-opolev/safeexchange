/// <summary>
/// TestFactory
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using SafeExchange.Tests.Utilities;

    public class TestFactory
    {
        public static TestFunctionContext FunctionContext = new TestFunctionContext();

        public static TestHttpRequestData CreateHttpRequestData(string requestMethod)
        {
            var request = new TestHttpRequestData(FunctionContext);
            request.SetMethod(requestMethod);

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
