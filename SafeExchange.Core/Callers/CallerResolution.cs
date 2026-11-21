/// <summary>
/// CallerResolution
/// </summary>

namespace SafeExchange.Core.Callers
{
    using System;
    using System.Diagnostics.CodeAnalysis;

    /// <summary>
    /// Either the resolved <see cref="Callers.Caller"/> or the <see cref="CallerDenial"/> to return instead.
    /// </summary>
    public sealed class CallerResolution
    {
        private CallerResolution(Caller? caller, CallerDenial? denial)
        {
            this.Caller = caller;
            this.Denial = denial;
            this.IsAllowed = caller is not null;
        }

        [MemberNotNullWhen(true, nameof(Caller))]
        [MemberNotNullWhen(false, nameof(Denial))]
        public bool IsAllowed { get; }

        public Caller? Caller { get; }

        public CallerDenial? Denial { get; }

        public static CallerResolution Ok(Caller caller)
            => new(caller ?? throw new ArgumentNullException(nameof(caller)), null);

        public static CallerResolution Denied(CallerDenial denial)
            => new(null, denial ?? throw new ArgumentNullException(nameof(denial)));
    }
}
