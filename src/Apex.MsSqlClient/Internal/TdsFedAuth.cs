using System.Buffers;
using System.Text;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsFedAuthLogin(
    string AccessToken,
    bool EchoFederatedAuthenticationRequired,
    ReadOnlyMemory<byte> Nonce);

internal readonly record struct TdsFedAuthInfo(string StsUrl, string ServicePrincipalName);

internal static class TdsFedAuth
{
    private const byte StsUrlInfoId = 0x01;
    private const byte ServicePrincipalNameInfoId = 0x02;
    private const int InfoOptionLength = 9;

    internal static byte[] EncodeLoginFeature(TdsFedAuthLogin login)
    {
        var token = EncodeToken(login.AccessToken);
        var nonce = login.Nonce.Span;
        ArrayBufferWriter<byte> writer = new(token.Length + nonce.Length + 10);
        writer.WriteByte(TdsFeatureId.FedAuth);
        writer.WriteInt32LittleEndian(checked(1 + sizeof(int) + token.Length + nonce.Length));
        writer.WriteByte(
          (byte)((TdsFedAuthLibrary.SecurityToken << 1) |
            (login.EchoFederatedAuthenticationRequired ? 1 : 0)));
        writer.WriteInt32LittleEndian(token.Length);
        writer.Write(token);
        writer.Write(nonce);
        return writer.WrittenSpan.ToArray();
    }

    internal static byte[] EncodeTokenMessage(TdsFedAuthLogin login)
    {
        var token = EncodeToken(login.AccessToken);
        var nonce = login.Nonce.Span;
        ArrayBufferWriter<byte> writer = new(token.Length + nonce.Length + 8);
        writer.WriteInt32LittleEndian(checked(sizeof(int) + token.Length + nonce.Length));
        writer.WriteInt32LittleEndian(token.Length);
        writer.Write(token);
        writer.Write(nonce);
        return writer.WrittenSpan.ToArray();
    }

    internal static TdsFedAuthInfo ParseInfo(ReadOnlySpan<byte> body)
    {
        TdsPayloadReader reader = new(body);
        var count = reader.ReadUInt32LittleEndian();
        if (count > (uint)(body.Length - sizeof(uint)) / InfoOptionLength)
        {
            throw new InvalidDataException(
              $"SQL Server FEDAUTHINFO token declares {count} options that do not fit the token.");
        }

        string? stsUrl = null;
        string? servicePrincipalName = null;
        for (var i = 0u; i < count; i++)
        {
            var id = reader.ReadByte();
            var dataLength = reader.ReadUInt32LittleEndian();
            var dataOffset = reader.ReadUInt32LittleEndian();
            if (dataOffset > (uint)body.Length ||
                dataLength > (uint)body.Length - dataOffset)
            {
                throw new InvalidDataException(
                  $"SQL Server FEDAUTHINFO option 0x{id:X2} points outside the token.");
            }

            var data = body.Slice((int)dataOffset, (int)dataLength);
            switch (id)
            {
                case StsUrlInfoId:
                    stsUrl = DecodeInfoData(data, "STSURL");
                    break;
                case ServicePrincipalNameInfoId:
                    servicePrincipalName = DecodeInfoData(data, "SPN");
                    break;
            }
        }

        if (string.IsNullOrEmpty(stsUrl) || string.IsNullOrEmpty(servicePrincipalName))
        {
            throw new InvalidDataException(
              "SQL Server FEDAUTHINFO token omitted the STSURL or SPN information.");
        }

        return new TdsFedAuthInfo(stsUrl, servicePrincipalName);
    }

    private static byte[] EncodeToken(string accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new ArgumentException(
              "The SQL Server federated authentication token cannot be empty.",
              nameof(accessToken));
        }

        return Encoding.Unicode.GetBytes(accessToken);
    }

    private static string DecodeInfoData(ReadOnlySpan<byte> data, string name) =>
      data.Length % 2 == 0
        ? Encoding.Unicode.GetString(data)
        : throw new InvalidDataException(
          $"SQL Server FEDAUTHINFO {name} data has an odd byte length.");
}
