/// <summary>
/// Incremental SHA-256 with serialisable state for multi-request hashing.
/// Follows FIPS 180-4. Cross-checked against System.Security.Cryptography.SHA256
/// in every test (see SerializableSha256Tests).
/// </summary>

namespace SafeExchange.Core.Crypto;

using System;

public sealed class SerializableSha256
{
    public const int StateSize = 106;

    public SerializableSha256() => throw new NotImplementedException();

    public void Append(ReadOnlySpan<byte> data) => throw new NotImplementedException();

    public byte[] Finish() => throw new NotImplementedException();

    public byte[] SaveState() => throw new NotImplementedException();

    public static SerializableSha256 Restore(byte[] state) => throw new NotImplementedException();
}
