

namespace SafeExchange.Tests.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.Azure.Functions.Worker.Context.Features;
    using Microsoft.Azure.Functions.Worker.OutputBindings;
    using Moq;
    using SafeExchange.Core.Configuration;

    public class TestFunctionContext : FunctionContext, IDisposable
    {
        private readonly FunctionInvocation _invocation;

        public TestFunctionContext()
            : this(new Mock<FunctionDefinition>().Object, new Mock<FunctionInvocation>().Object)
        {
        }

        public TestFunctionContext(FunctionDefinition functionDefinition, FunctionInvocation invocation)
        {
            FunctionDefinition = functionDefinition;
            _invocation = invocation;

            Features = new Mock<IInvocationFeatures>().Object;

            this.BindingContext = new Mock<BindingContext>().Object;
            this.RetryContext = new Mock<RetryContext>().Object;
        }

        public bool IsDisposed { get; private set; }

        public override IServiceProvider InstanceServices { get; set; }

        public override FunctionDefinition FunctionDefinition { get; }

        public override IDictionary<object, object> Items { get; set; }

        public override IInvocationFeatures Features { get; }

        public override string InvocationId => _invocation.Id;

        public override string FunctionId => _invocation.FunctionId;

        public override TraceContext TraceContext => _invocation.TraceContext;

        public override BindingContext BindingContext { get; }

        public override RetryContext RetryContext { get; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
