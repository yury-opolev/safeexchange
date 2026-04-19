/// <summary>
/// Incremental SHA-256 with serialisable state for multi-request hashing.
/// Follows FIPS 180-4. Cross-checked against System.Security.Cryptography.SHA256
/// in every test (see SerializableSha256Tests).
///
/// State binary format (version 1, 106 bytes):
///   offset 0,   1 byte:   version marker (0x01)
///   offset 1,   32 bytes: H0..H7 as 8 uint32 big-endian
///   offset 33,  1 byte:   partial-block byte count (0..63)
///   offset 34,  64 bytes: partial-block buffer (zero-padded if partial)
///   offset 98,  8 bytes:  total bit count as uint64 big-endian
/// </summary>

namespace SafeExchange.Core.Crypto;

using System;
using System.Buffers.Binary;

public sealed class SerializableSha256
{
    public const int StateSize = 106;
    private const byte StateVersion = 0x01;
    private const int BlockSize = 64;

    private static readonly uint[] InitialH = new uint[]
    {
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
        0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    };

    private static readonly uint[] K = new uint[]
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    private readonly uint[] h;
    private readonly byte[] partial;
    private int partialLength;
    private ulong totalBits;

    public SerializableSha256()
    {
        this.h = (uint[])InitialH.Clone();
        this.partial = new byte[BlockSize];
        this.partialLength = 0;
        this.totalBits = 0;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        this.totalBits += (ulong)data.Length * 8UL;

        if (this.partialLength > 0)
        {
            var need = BlockSize - this.partialLength;
            if (data.Length < need)
            {
                data.CopyTo(this.partial.AsSpan(this.partialLength));
                this.partialLength += data.Length;
                return;
            }

            data.Slice(0, need).CopyTo(this.partial.AsSpan(this.partialLength));
            this.ProcessBlock(this.partial);
            this.partialLength = 0;
            data = data.Slice(need);
        }

        while (data.Length >= BlockSize)
        {
            this.ProcessBlock(data.Slice(0, BlockSize));
            data = data.Slice(BlockSize);
        }

        if (data.Length > 0)
        {
            data.CopyTo(this.partial.AsSpan());
            this.partialLength = data.Length;
        }
    }

    public byte[] Finish()
    {
        Span<byte> padBuffer = stackalloc byte[BlockSize * 2];
        var bitLen = this.totalBits;

        this.partial.AsSpan(0, this.partialLength).CopyTo(padBuffer);
        padBuffer[this.partialLength] = 0x80;
        var padEnd = this.partialLength + 1;

        var totalNeeded = padEnd + 8;
        var paddedLen = totalNeeded <= BlockSize ? BlockSize : BlockSize * 2;
        padBuffer.Slice(padEnd, paddedLen - padEnd - 8).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(padBuffer.Slice(paddedLen - 8, 8), bitLen);

        this.ProcessBlock(padBuffer.Slice(0, BlockSize));
        if (paddedLen == BlockSize * 2)
        {
            this.ProcessBlock(padBuffer.Slice(BlockSize, BlockSize));
        }

        var digest = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4, 4), this.h[i]);
        }

        return digest;
    }

    public byte[] SaveState()
    {
        var state = new byte[StateSize];
        state[0] = StateVersion;
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(state.AsSpan(1 + i * 4, 4), this.h[i]);
        }

        state[33] = (byte)this.partialLength;
        this.partial.AsSpan(0, this.partialLength).CopyTo(state.AsSpan(34));
        if (this.partialLength < BlockSize)
        {
            state.AsSpan(34 + this.partialLength, BlockSize - this.partialLength).Clear();
        }

        BinaryPrimitives.WriteUInt64BigEndian(state.AsSpan(98, 8), this.totalBits);
        return state;
    }

    public static SerializableSha256 Restore(byte[] state)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        if (state.Length != StateSize)
        {
            throw new ArgumentException($"State length must be {StateSize}.", nameof(state));
        }

        if (state[0] != StateVersion)
        {
            throw new ArgumentException($"Unsupported state version: {state[0]:X2}.", nameof(state));
        }

        var instance = new SerializableSha256();
        for (var i = 0; i < 8; i++)
        {
            instance.h[i] = BinaryPrimitives.ReadUInt32BigEndian(state.AsSpan(1 + i * 4, 4));
        }

        instance.partialLength = state[33];
        if (instance.partialLength < 0 || instance.partialLength >= BlockSize)
        {
            throw new ArgumentException("Invalid partial length in state.", nameof(state));
        }

        state.AsSpan(34, BlockSize).CopyTo(instance.partial);
        instance.totalBits = BinaryPrimitives.ReadUInt64BigEndian(state.AsSpan(98, 8));
        return instance;
    }

    private void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<uint> w = stackalloc uint[64];
        for (var i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));
        }

        for (var i = 16; i < 64; i++)
        {
            var s0 = RotateRight(w[i - 15], 7) ^ RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
            var s1 = RotateRight(w[i - 2], 17) ^ RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        var a = this.h[0];
        var b = this.h[1];
        var c = this.h[2];
        var d = this.h[3];
        var e = this.h[4];
        var f = this.h[5];
        var g = this.h[6];
        var hh = this.h[7];

        for (var i = 0; i < 64; i++)
        {
            var s1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
            var ch = (e & f) ^ (~e & g);
            var temp1 = hh + s1 + ch + K[i] + w[i];
            var s0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
            var maj = (a & b) ^ (a & c) ^ (b & c);
            var temp2 = s0 + maj;

            hh = g;
            g = f;
            f = e;
            e = d + temp1;
            d = c;
            c = b;
            b = a;
            a = temp1 + temp2;
        }

        this.h[0] += a;
        this.h[1] += b;
        this.h[2] += c;
        this.h[3] += d;
        this.h[4] += e;
        this.h[5] += f;
        this.h[6] += g;
        this.h[7] += hh;
    }

    private static uint RotateRight(uint value, int bits) => (value >> bits) | (value << (32 - bits));
}
