/// <summary>
/// ApiVersionTelemetryMiddlewareTests
///
/// Covers the "Propositions - 01" item 3 change: the per-request API-version trace is emitted
/// at Debug (not Information), because the saex.apiVersion custom dimension already reaches
/// request telemetry via ApiVersionTelemetryInitializer. Also pins the AsyncLocal contract:
/// the version is visible to telemetry during the invocation and restored afterwards.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Extensions.Logging;
    using Moq;
    using NUnit.Framework;
    using SafeExchange.Core.Middleware;
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    [TestFixture]
    public class ApiVersionTelemetryMiddlewareTests
    {
        private RecordingLevelLogger logger = null!;
        private ApiVersionTelemetryMiddleware middleware = null!;

        [SetUp]
        public void Setup()
        {
            this.logger = new RecordingLevelLogger();
            this.middleware = new ApiVersionTelemetryMiddleware(this.logger);
        }

        [Test]
        public async Task Invoke_VersionedRequest_EmitsTraceAtDebugOnly()
        {
            var context = CreateContext(new Dictionary<string, object?> { ["apiVersion"] = "v3" });

            await this.middleware.Invoke(context, _ => Task.CompletedTask);

            var apiVersionRecords = this.logger.Records.FindAll(r => r.Message.Contains("saex api version"));
            Assert.That(apiVersionRecords, Has.Count.EqualTo(1), "One trace per versioned request.");
            Assert.That(apiVersionRecords[0].Level, Is.EqualTo(LogLevel.Debug),
                "The per-request trace must stay below Information so it carries no ingestion cost by default.");
        }

        [Test]
        public async Task Invoke_StampsVersionDuringInvocation_AndRestoresAfter()
        {
            var context = CreateContext(new Dictionary<string, object?> { ["apiVersion"] = "v2" });

            string? observedDuringNext = null;
            await this.middleware.Invoke(context, _ =>
            {
                observedDuringNext = ApiVersionTelemetryMiddleware.Current;
                return Task.CompletedTask;
            });

            Assert.That(observedDuringNext, Is.EqualTo("v2"));
            Assert.That(ApiVersionTelemetryMiddleware.Current, Is.Null, "The previous AsyncLocal value is restored in finally.");
        }

        [Test]
        public async Task Invoke_RestoresVersionEvenWhenNextThrows()
        {
            var context = CreateContext(new Dictionary<string, object?> { ["apiVersion"] = "v3" });

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await this.middleware.Invoke(context, _ => throw new InvalidOperationException("boom")));

            Assert.That(ApiVersionTelemetryMiddleware.Current, Is.Null);
            await Task.CompletedTask;
        }

        [Test]
        public async Task Invoke_NoApiVersionRouteValue_EmitsNothingAndLeavesCurrentUnset()
        {
            var context = CreateContext(new Dictionary<string, object?>());

            string? observedDuringNext = "sentinel";
            await this.middleware.Invoke(context, _ =>
            {
                observedDuringNext = ApiVersionTelemetryMiddleware.Current;
                return Task.CompletedTask;
            });

            Assert.That(observedDuringNext, Is.Null);
            Assert.That(this.logger.Records, Is.Empty);
        }

        [Test]
        public async Task Initializer_StampsDimensionDuringInvocation_ButNotOutside()
        {
            var context = CreateContext(new Dictionary<string, object?> { ["apiVersion"] = "v3" });
            var initializer = new ApiVersionTelemetryInitializer();

            var insideTelemetry = new TraceTelemetry("inside");
            await this.middleware.Invoke(context, _ =>
            {
                initializer.Initialize(insideTelemetry);
                return Task.CompletedTask;
            });

            var outsideTelemetry = new TraceTelemetry("outside");
            initializer.Initialize(outsideTelemetry);

            Assert.That(insideTelemetry.Properties[ApiVersionTelemetryMiddleware.PropertyName], Is.EqualTo("v3"),
                "Telemetry emitted during the invocation carries the saex.apiVersion dimension.");
            Assert.That(outsideTelemetry.Properties.ContainsKey(ApiVersionTelemetryMiddleware.PropertyName), Is.False);
        }

        private static FunctionContext CreateContext(IDictionary<string, object?> bindingData)
        {
            var bindingContext = Mock.Of<BindingContext>(b =>
                b.BindingData == (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(bindingData));
            var definition = Mock.Of<FunctionDefinition>(d => d.Name == "TestFunction");

            var contextMock = new Mock<FunctionContext>();
            contextMock.SetupGet(c => c.BindingContext).Returns(bindingContext);
            contextMock.SetupGet(c => c.FunctionDefinition).Returns(definition);
            return contextMock.Object;
        }

        private sealed class RecordingLevelLogger : ILogger<ApiVersionTelemetryMiddleware>
        {
            public List<(LogLevel Level, string Message)> Records { get; } = new();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => this.Records.Add((logLevel, formatter(state, exception)));
        }
    }
}
