using System.Security.Cryptography;
using System.Text;

namespace Apex.MySqlClient.Internal;

/// <summary>Implements the password scrambles of the MySQL authentication plugins.</summary>
internal static class MySqlAuthentication
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);

    internal static byte[] GetPasswordBytes(string password) =>
      password.Length == 0 ? [] : s_utf8.GetBytes(password);

    /// <summary>
    /// Computes the <c>mysql_native_password</c> response,
    /// <c>SHA1(password) XOR SHA1(nonce + SHA1(SHA1(password)))</c>.
    /// </summary>
    internal static byte[] ScrambleNativePassword(ReadOnlySpan<byte> password, ReadOnlySpan<byte> nonce)
    {
        if (password.IsEmpty)
        {
            return [];
        }

        Span<byte> first = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(password, first);
        Span<byte> second = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(first, second);

        Span<byte> combined = stackalloc byte[nonce.Length + second.Length];
        nonce.CopyTo(combined);
        second.CopyTo(combined[nonce.Length..]);
        Span<byte> third = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(combined, third);

        var result = new byte[SHA1.HashSizeInBytes];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(first[i] ^ third[i]);
        }

        return result;
    }

    /// <summary>
    /// Computes the <c>caching_sha2_password</c> response,
    /// <c>SHA256(password) XOR SHA256(SHA256(SHA256(password)) + nonce)</c>.
    /// </summary>
    internal static byte[] ScrambleCachingSha2Password(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> nonce)
    {
        if (password.IsEmpty)
        {
            return [];
        }

        Span<byte> first = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(password, first);
        Span<byte> second = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(first, second);

        Span<byte> combined = stackalloc byte[second.Length + nonce.Length];
        second.CopyTo(combined);
        nonce.CopyTo(combined[second.Length..]);
        Span<byte> third = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(combined, third);

        var result = new byte[SHA256.HashSizeInBytes];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(first[i] ^ third[i]);
        }

        return result;
    }

    /// <summary>Builds the null terminated cleartext password sent over a secure channel.</summary>
    internal static byte[] GetNullTerminatedPassword(ReadOnlySpan<byte> password)
    {
        var result = new byte[password.Length + 1];
        password.CopyTo(result);
        return result;
    }

    /// <summary>
    /// Encrypts the null terminated password, obfuscated with the server nonce, using the RSA
    /// public key the server provided. MySQL uses OAEP with SHA-1.
    /// </summary>
    internal static byte[] EncryptPassword(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> nonce,
        string publicKeyPem)
    {
        if (nonce.IsEmpty)
        {
            throw new InvalidDataException("The MySQL authentication nonce is missing.");
        }

        var obfuscated = GetNullTerminatedPassword(password);
        for (var i = 0; i < obfuscated.Length; i++)
        {
            obfuscated[i] ^= nonce[i % nonce.Length];
        }

        using RSA rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The MySQL server sent an invalid RSA public key.", exception);
        }

        try
        {
            return rsa.Encrypt(obfuscated, RSAEncryptionPadding.OaepSHA1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(obfuscated);
        }
    }
}
