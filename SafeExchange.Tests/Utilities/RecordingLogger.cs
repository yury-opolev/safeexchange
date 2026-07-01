/// <summary>
/// RecordingLogger — a test ILogger that captures every log entry (level, formatted
/// message, exception) so tests can assert that a diagnostic was actually emitted.
/// Used to verify observability fixes, e.g. that a call site passes a logger through
/// so a malformed config produces a trace instead of failing silently.
/// </summary>

namespace SafeExchange.Tests
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class RecordingLogger : ILogger
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        public List<Entry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception exception,
                                Func<TState, Exception, string> formatter)
        {
            this.Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }

        /// <summary>True if any recorded entry at the given level contains the substring.</summary>
        public bool Has(LogLevel level, string containing)
            => this.Entries.Any(e => e.Level == level
                && e.Message.Contains(containing, StringComparison.OrdinalIgnoreCase));
    }
}
