/// <summary>
/// StablePartitionKey
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System;
    using System.Buffers.Binary;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;

    public static class StablePartitionKey
    {
        /// <summary>
        /// Derives a 4-digit partition key from <paramref name="value"/>. Deterministic across processes and instances;
        /// <see cref="string.GetHashCode()"/> must not be used for this because it is randomized per process.
        /// </summary>
        public static string From(string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return (BinaryPrimitives.ReadUInt32BigEndian(digest) % 10000).ToString("D4", CultureInfo.InvariantCulture);
        }
    }
}
