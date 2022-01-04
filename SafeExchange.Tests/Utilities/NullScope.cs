/// <summary>
/// NullScope
/// </summary>

namespace SafeExchange.Tests
{
    using System;
    using System.Collections.Generic;

    public class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();

        private NullScope() { }

        public void Dispose() { }
    }
}
