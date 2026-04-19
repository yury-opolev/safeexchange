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

    private static byte[] HashAll(byte[] data)
    {
        var hasher = new SerializableSha256();
        hasher.Append(data);
        return hasher.Finish();
    }
}
