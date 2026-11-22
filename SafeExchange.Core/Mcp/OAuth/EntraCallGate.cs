/// <summary>
/// EntraCallGate
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Caps the concurrent outbound Entra token calls of this instance (<c>Mcp:OAuth:RateLimit:MaxConcurrentEntraCalls</c>,
    /// default 16). A call that finds no free slot within <see cref="DefaultWait"/> is turned away (503,
    /// <c>Retry-After: 2</c>) instead of queueing behind a slow Entra.
    /// </summary>
    public sealed class EntraCallGate
    {
        public const int RetryAfterSeconds = 2;

        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

        private readonly SemaphoreSlim slots;

        private readonly TimeSpan wait;

        public EntraCallGate(int maxConcurrentCalls, TimeSpan wait)
        {
            var slotCount = Math.Max(1, maxConcurrentCalls);
            this.slots = new SemaphoreSlim(slotCount, slotCount);
            this.wait = wait;
        }

        /// <summary>
        /// True when a slot was taken; the caller must then call <see cref="Exit"/> exactly once.
        /// </summary>
        public Task<bool> TryEnterAsync(CancellationToken cancellationToken = default) => this.slots.WaitAsync(this.wait, cancellationToken);

        public void Exit() => this.slots.Release();
    }
}
