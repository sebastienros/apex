using System.Security.Cryptography;
using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlAuthenticationTests
{
    private static readonly byte[] s_nonce = Enumerable.Range(1, 20).Select(static i => (byte)i).ToArray();

    [TestMethod]
    public void ScramblesNativePasswordUsingSha1DoubleHash()
    {
        var password = MySqlAuthentication.GetPasswordBytes("secret");

        var scramble = MySqlAuthentication.ScrambleNativePassword(password, s_nonce);

        CollectionAssert.AreEqual(
          Convert.FromHexString("B32BB3A583E1340C0A1108D58B1BE49781AD8C2F"),
          scramble);
    }

    [TestMethod]
    public void EmptyPasswordProducesEmptyNativeScramble()
    {
        var scramble = MySqlAuthentication.ScrambleNativePassword([], s_nonce);

        Assert.AreEqual(0, scramble.Length);
    }

    [TestMethod]
    public void ScramblesCachingSha2PasswordUsingSha256TripleHash()
    {
        var password = MySqlAuthentication.GetPasswordBytes("secret");

        var scramble = MySqlAuthentication.ScrambleCachingSha2Password(password, s_nonce);

        CollectionAssert.AreEqual(
          Convert.FromHexString("746EBE205D56A0707ACB3E796E834E0DD7B1D61743B26BD5202C7A623230C7C9"),
          scramble);
    }

    [TestMethod]
    public void EmptyPasswordProducesEmptyCachingSha2Scramble()
    {
        var scramble = MySqlAuthentication.ScrambleCachingSha2Password([], s_nonce);

        Assert.AreEqual(0, scramble.Length);
    }

    [TestMethod]
    public void NullTerminatedPasswordAppendsSingleZeroByte()
    {
        var password = MySqlAuthentication.GetPasswordBytes("secret");

        var result = MySqlAuthentication.GetNullTerminatedPassword(password);

        Assert.AreEqual(password.Length + 1, result.Length);
        Assert.AreEqual((byte)0, result[^1]);
        CollectionAssert.AreEqual(password, result[..^1]);
    }

    [TestMethod]
    public void EncryptsPasswordWithRsaOaepUsingServerPublicKey()
    {
        using RSA rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        var password = MySqlAuthentication.GetPasswordBytes("secret");

        var encrypted = MySqlAuthentication.EncryptPassword(password, s_nonce, publicKeyPem);

        Assert.AreEqual(256, encrypted.Length);
        var decrypted = rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);
        var expectedObfuscated = MySqlAuthentication.GetNullTerminatedPassword(password);
        for (var i = 0; i < expectedObfuscated.Length; i++)
        {
            expectedObfuscated[i] ^= s_nonce[i % s_nonce.Length];
        }

        CollectionAssert.AreEqual(expectedObfuscated, decrypted);
    }

    [TestMethod]
    public void EncryptPasswordRejectsEmptyNonce()
    {
        using RSA rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        Assert.ThrowsExactly<InvalidDataException>(() =>
          MySqlAuthentication.EncryptPassword([1, 2, 3], [], publicKeyPem));
    }

    [TestMethod]
    public void EncryptPasswordRejectsInvalidPem()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
          MySqlAuthentication.EncryptPassword([1, 2, 3], s_nonce, "not a pem"));
    }
}
