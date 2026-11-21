/// <summary>
/// RecordingLoggerProvider
/// </summary>

namespace SafeExchange.Tests.Utilities
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Captures every log entry of every category (formatted message and exception text) so a test can assert
    /// what was, and was not, logged.
    /// </summary>
    public sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> entries = new();

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (this.entries)
                {
                    return this.entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingCategoryLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(string entry)
        {
            lock (this.entries)
            {
                this.entries.Add(entry);
            }
        }

        private sealed class RecordingCategoryLogger : ILogger
        {
            private readonly RecordingLoggerProvider provider;

            private readonly string categoryName;

            public RecordingCategoryLogger(RecordingLoggerProvider provider, string categoryName)
            {
                this.provider = provider;
                this.categoryName = categoryName;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => this.provider.Add($"{logLevel} {this.categoryName}: {formatter(state, exception)} {exception}");
        }
    }
}
