/// <summary>
/// ClientRateLimiter
/// </summary>

namespace SafeExchange.Core.Mcp.OAuth
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// In-memory token buckets keyed by an arbitrary string (endpoint + client address). A bucket starts full
    /// (<c>burst</c> tokens), refills at <c>permitsPerMinute</c> and each request takes one token. At most
    /// <c>maxKeys</c> buckets are kept; the least recently used one is dropped to make room. Thread-safe; the caller
    /// passes the clock.
    /// </summary>
    public sealed class ClientRateLimiter
    {
        private readonly int maxKeys;

        private readonly Dictionary<string, LinkedListNode<Bucket>> buckets = new(StringComparer.Ordinal);

        // Most recently used first.
        private readonly LinkedList<Bucket> recency = new();

        private readonly object sync = new();

        public ClientRateLimiter(int maxKeys)
        {
            this.maxKeys = Math.Max(1, maxKeys);
        }

        public int Count
        {
            get
            {
                lock (this.sync)
                {
                    return this.buckets.Count;
                }
            }
        }

        public bool TryAcquire(string key, int permitsPerMinute, int burst, DateTime utcNow, out TimeSpan retryAfter)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentOutOfRangeException.ThrowIfLessThan(permitsPerMinute, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);

            var perSecond = permitsPerMinute / 60.0;
            lock (this.sync)
            {
                if (this.buckets.TryGetValue(key, out var node))
                {
                    this.recency.Remove(node);
                    this.recency.AddFirst(node);
                    var bucket = node.Value;
                    if (utcNow > bucket.UpdatedAt)
                    {
                        bucket.Tokens = Math.Min(burst, bucket.Tokens + ((utcNow - bucket.UpdatedAt).TotalSeconds * perSecond));
                        bucket.UpdatedAt = utcNow;
                    }
                }
                else
                {
                    if (this.buckets.Count >= this.maxKeys)
                    {
                        this.buckets.Remove(this.recency.Last!.Value.Key);
                        this.recency.RemoveLast();
                    }

                    node = this.recency.AddFirst(new Bucket(key, burst, utcNow));
                    this.buckets[key] = node;
                }

                if (node.Value.Tokens >= 1)
                {
                    node.Value.Tokens -= 1;
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                retryAfter = TimeSpan.FromSeconds((1 - node.Value.Tokens) / perSecond);
                return false;
            }
        }

        private sealed class Bucket
        {
            public Bucket(string key, double tokens, DateTime updatedAt)
            {
                this.Key = key;
                this.Tokens = tokens;
                this.UpdatedAt = updatedAt;
            }

            public string Key { get; }

            public double Tokens { get; set; }

            public DateTime UpdatedAt { get; set; }
        }
    }
}
