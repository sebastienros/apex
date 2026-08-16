using System.Security.Cryptography;
using System.Text;

namespace Apex.PgClient.Internal;

internal sealed class PgScramClient
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly string _password;
    private readonly string _clientNonce;
    private readonly string _clientFirstBare;
    private readonly string _gs2Header;
    private readonly byte[]? _channelBindingData;
    private byte[]? _expectedServerSignature;

    public PgScramClient(
        string username,
        string password,
        byte[]? channelBindingData = null,
        bool advertiseChannelBinding = false)
    {
        _password = password;
        _channelBindingData = channelBindingData;
        _gs2Header = channelBindingData is not null
          ? "p=tls-server-end-point,,"
          : advertiseChannelBinding
            ? "y,,"
            : "n,,";
        _clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        _clientFirstBare = $"n={Escape(username)},r={_clientNonce}";
    }

    public string ClientFirstMessage => _gs2Header + _clientFirstBare;

    public string HandleServerFirst(string serverFirst)
    {
        var fields = ParseFields(serverFirst);
        var nonce = GetRequired(fields, 'r');
        if (!nonce.StartsWith(_clientNonce, StringComparison.Ordinal) || nonce.Length == _clientNonce.Length)
        {
            throw new InvalidDataException("The PostgreSQL SCRAM server nonce is invalid.");
        }

        var salt = Convert.FromBase64String(GetRequired(fields, 's'));
        if (!int.TryParse(GetRequired(fields, 'i'), out var iterations) || iterations <= 0)
        {
            throw new InvalidDataException("The PostgreSQL SCRAM iteration count is invalid.");
        }

        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            _password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);
        var clientKey = Hmac(saltedPassword, "Client Key");
        var storedKey = SHA256.HashData(clientKey);

        var gs2Header = s_utf8.GetBytes(_gs2Header);
        var binding = _channelBindingData is null
          ? gs2Header
          : [.. gs2Header, .. _channelBindingData];
        var clientFinalWithoutProof = $"c={Convert.ToBase64String(binding)},r={nonce}";
        var authMessage = $"{_clientFirstBare},{serverFirst},{clientFinalWithoutProof}";
        var clientSignature = Hmac(storedKey, authMessage);
        var proof = Xor(clientKey, clientSignature);

        var serverKey = Hmac(saltedPassword, "Server Key");
        _expectedServerSignature = Hmac(serverKey, authMessage);
        CryptographicOperations.ZeroMemory(saltedPassword);
        CryptographicOperations.ZeroMemory(clientKey);

        return $"{clientFinalWithoutProof},p={Convert.ToBase64String(proof)}";
    }

    public void HandleServerFinal(string serverFinal)
    {
        var fields = ParseFields(serverFinal);
        if (fields.TryGetValue('e', out var error))
        {
            throw new InvalidDataException($"PostgreSQL SCRAM authentication failed: {error}");
        }

        var actual = Convert.FromBase64String(GetRequired(fields, 'v'));
        if (_expectedServerSignature is null ||
            !CryptographicOperations.FixedTimeEquals(actual, _expectedServerSignature))
        {
            throw new InvalidDataException("The PostgreSQL SCRAM server signature is invalid.");
        }
    }

    private static byte[] Hmac(ReadOnlySpan<byte> key, string text) =>
        HMACSHA256.HashData(key, s_utf8.GetBytes(text));

    private static byte[] Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = GC.AllocateUninitializedArray<byte>(left.Length);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(left[i] ^ right[i]);
        }

        return result;
    }

    private static Dictionary<char, string> ParseFields(string message)
    {
        Dictionary<char, string> fields = [];
        foreach (var field in message.Split(','))
        {
            if (field.Length < 3 || field[1] != '=')
            {
                throw new InvalidDataException("The PostgreSQL SCRAM message is malformed.");
            }

            fields.Add(field[0], field[2..]);
        }

        return fields;
    }

    private static string GetRequired(IReadOnlyDictionary<char, string> fields, char key) =>
        fields.TryGetValue(key, out var value)
            ? value
            : throw new InvalidDataException($"The PostgreSQL SCRAM field '{key}' is missing.");

    private static string Escape(string username) =>
        username.Replace("=", "=3D", StringComparison.Ordinal).Replace(",", "=2C", StringComparison.Ordinal);
}
