/// <summary>
/// TestFactory
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Moq;
    using SafeExchange.Tests.Utilities;

    public class TestFactory
    {
        public static TestHttpRequestData CreateHttpRequestData(string requestMethod)
        {
            var context = new Mock<FunctionContext>();
            var request = new TestHttpRequestData(context.Object);
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
