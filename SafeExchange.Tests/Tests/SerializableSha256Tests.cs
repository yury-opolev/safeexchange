/// <summary>
/// SerializableSha256 tests - NIST FIPS 180-4 vectors + BCL cross-check +
/// state-serialisation properties.
/// </summary>

namespace SafeExchange.Tests.Tests;

using System;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SafeExchange.Core.Crypto;

[TestFixture]
public class SerializableSha256Tests
{
    // FIPS 180-4 test vectors.
    private const string EmptyHex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string AbcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Test]
    public void HashAll_EmptyInput_MatchesFipsVector()
    {
        var hash = Convert.ToHexString(HashAll(Array.Empty<byte>())).ToLowerInvariant();
        Assert.That(hash, Is.EqualTo(EmptyHex));
    }

    [Test]
    public void HashAll_Abc_MatchesFipsVector()
    {
        var hash = Convert.ToHexString(HashAll(Encoding.ASCII.GetBytes("abc"))).ToLowerInvariant();
        Assert.That(hash, Is.EqualTo(AbcHex));
    }

    [Test]
    public void HashAll_448BitMessage_MatchesFipsVector()
    {
        // "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq" (56 bytes)
        var input = Encoding.ASCII.GetBytes("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq");
        var hash = Convert.ToHexString(HashAll(input)).ToLowerInvariant();
        Assert.That(hash, Is.EqualTo("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"));
    }

    [Test]
    public void HashAll_OneMillionAs_MatchesFipsVector()
    {
        var input = new byte[1_000_000];
        Array.Fill(input, (byte)'a');
        var hash = Convert.ToHexString(HashAll(input)).ToLowerInvariant();
        Assert.That(hash, Is.EqualTo("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"));
    }

    [Test]
    public void HashAll_BoundaryLengths_MatchBcl()
    {
        int[] lengths = new[] { 0, 1, 55, 56, 63, 64, 65, 127, 128, 129, 4096 };
        foreach (var length in lengths)
        {
            var input = new byte[length];
            for (var i = 0; i < length; i++)
            {
                input[i] = (byte)(i & 0xff);
            }

            var ours = HashAll(input);
            var bcl = SHA256.HashData(input);
            Assert.That(ours, Is.EqualTo(bcl), $"Divergence from BCL at length {length}.");
        }
    }

    [Test]
    public void Append_SplitAtEverySplitPoint_MatchesWholeHash()
    {
        var rng = new Random(42);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var length = rng.Next(1, 4096);
            var input = new byte[length];
            rng.NextBytes(input);
            var expected = SHA256.HashData(input);

            for (var splitPoint = 0; splitPoint <= length; splitPoint++)
            {
                var hasher = new SerializableSha256();
                hasher.Append(input.AsSpan(0, splitPoint));
                hasher.Append(input.AsSpan(splitPoint));
                Assert.That(hasher.Finish(), Is.EqualTo(expected),
                    $"Divergence at split {splitPoint}/{length}, iteration {iteration}.");
            }
        }
    }

    [Test]
    public void SaveState_RestoreAtEverySplitPoint_MatchesWholeHash()
    {
        var rng = new Random(123);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var length = rng.Next(1, 4096);
            var input = new byte[length];
            rng.NextBytes(input);
            var expected = SHA256.HashData(input);

            for (var splitPoint = 0; splitPoint <= length; splitPoint++)
            {
                var first = new SerializableSha256();
                first.Append(input.AsSpan(0, splitPoint));
                var state = first.SaveState();

                var second = SerializableSha256.Restore(state);
                second.Append(input.AsSpan(splitPoint));
                Assert.That(second.Finish(), Is.EqualTo(expected),
                    $"Save/restore divergence at split {splitPoint}/{length}, iteration {iteration}.");
            }
        }
    }

    [Test]
    public void SaveRestoreSaveRestore_DoesNotCorruptState()
    {
        var rng = new Random(7);
        var input = new byte[8192];
        rng.NextBytes(input);
        var expected = SHA256.HashData(input);

        var hasher = new SerializableSha256();
        var pos = 0;
        while (pos < input.Length)
        {
            var step = Math.Min(rng.Next(1, 256), input.Length - pos);
            hasher.Append(input.AsSpan(pos, step));
            var state = hasher.SaveState();
            hasher = SerializableSha256.Restore(state);
            pos += step;
        }

        Assert.That(hasher.Finish(), Is.EqualTo(expected));
    }

    [Test]
    public void Restore_InvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => SerializableSha256.Restore(new byte[105]));
    }

    [Test]
    public void Restore_InvalidVersion_Throws()
    {
        var state = new SerializableSha256().SaveState();
        state[0] = 0x99;
        Assert.Throws<ArgumentException>(() => SerializableSha256.Restore(state));
    }

    [Test]
    public void Restore_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SerializableSha256.Restore(null!));
    }

    [Test]
    public void HashAll_Fuzz100kRandomInputs_MatchesBcl()
    {
        var rng = new Random(9001);
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var length = rng.Next(1, 100_000);
            var input = new byte[length];
            rng.NextBytes(input);
            var ours = HashAll(input);
            var bcl = SHA256.HashData(input);
            Assert.That(ours, Is.EqualTo(bcl), $"Divergence at iteration {iteration}, length {length}.");
        }
    }

    private static byte[] HashAll(byte[] data)
    {
        var hasher = new SerializableSha256();
        hasher.Append(data);
        return hasher.Finish();
    }
}
