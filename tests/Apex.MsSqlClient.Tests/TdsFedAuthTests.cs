using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsFedAuthTests
{
    private const string AccessToken = "eyJhbGciOiJub25lIn0.header.payload";

    [TestMethod]
    public void PreLoginAdvertisesFederatedAuthenticationOnlyWhenRequested()
    {
        var standard = TdsPreLogin.Encode(TdsEncryptionLevel.On);
        var federated = TdsPreLogin.Encode(TdsEncryptionLevel.On, requestFederatedAuthentication: true);

        Assert.AreEqual(24, standard.Length);
        Assert.AreEqual(30, federated.Length);
        Assert.IsFalse(TdsPreLogin.Parse(standard).FederatedAuthenticationRequired);
        Assert.IsTrue(TdsPreLogin.Parse(federated).FederatedAuthenticationRequired);
        Assert.AreEqual(0x06, federated[15]);
        Assert.AreEqual(0xFF, federated[20]);
        Assert.AreEqual(1, federated[^1]);
    }

    [TestMethod]
    public void PreLoginParsesFederatedAuthenticationAndNonce()
    {
        var nonce = new byte[TdsPreLogin.NonceLength];
        nonce.AsSpan().Fill(0x5A);
        var response = TdsPreLogin.Parse(
          BuildPreLoginResponse(federatedAuthentication: 1, nonce));

        Assert.IsTrue(response.FederatedAuthenticationRequired);
        CollectionAssert.AreEqual(nonce, response.Nonce.ToArray());
    }

    [TestMethod]
    public void PreLoginRejectsInvalidFederatedAuthenticationValue()
    {
        var payload = BuildPreLoginResponse(federatedAuthentication: 2, nonce: null);

        var error = Assert.ThrowsExactly<InvalidDataException>(() => TdsPreLogin.Parse(payload));
        StringAssert.Contains(error.Message, "FEDAUTHREQUIRED");
    }

    [TestMethod]
    public void PreLoginRejectsNonceWithInvalidLength()
    {
        var payload = BuildPreLoginResponse(federatedAuthentication: 1, nonce: new byte[16]);

        Assert.ThrowsExactly<InvalidDataException>(() => TdsPreLogin.Parse(payload));
    }

    [TestMethod]
    public void Login7CarriesSecurityTokenFeatureAndOmitsCredentials()
    {
        MsSqlConnectOptions options = new()
        {
            Host = "db",
            Username = "app",
            Password = "S3cret!",
            Database = "catalog",
            WorkstationId = "host",
        };

        var login = TdsLogin7.Encode(
          options,
          new TdsFedAuthLogin(AccessToken, EchoFederatedAuthenticationRequired: true, default));

        Assert.AreEqual(login.Length, BinaryPrimitives.ReadInt32LittleEndian(login));
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(42)));
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(46)));

        var features = ReadFeatureExtension(login);
        TdsPayloadReader reader = new(features);
        Assert.AreEqual(TdsFeatureId.FedAuth, reader.ReadByte());
        var expectedToken = Encoding.Unicode.GetBytes(AccessToken);
        Assert.AreEqual(expectedToken.Length + 5, reader.ReadInt32LittleEndian());
        Assert.AreEqual((TdsFedAuthLibrary.SecurityToken << 1) | 1, reader.ReadByte());
        Assert.AreEqual(expectedToken.Length, reader.ReadInt32LittleEndian());
        CollectionAssert.AreEqual(expectedToken, reader.ReadSpan(expectedToken.Length).ToArray());
        Assert.AreEqual(TdsFeatureId.JsonSupport, reader.ReadByte());
    }

    [TestMethod]
    public void Login7EchoesServerNonceAndClearsEchoFlag()
    {
        var nonce = new byte[TdsPreLogin.NonceLength];
        nonce.AsSpan().Fill(0x2B);
        MsSqlConnectOptions options = new() { Host = "db", WorkstationId = "host" };

        var login = TdsLogin7.Encode(
          options,
          new TdsFedAuthLogin(AccessToken, EchoFederatedAuthenticationRequired: false, nonce));

        TdsPayloadReader reader = new(ReadFeatureExtension(login));
        Assert.AreEqual(TdsFeatureId.FedAuth, reader.ReadByte());
        var tokenBytes = Encoding.Unicode.GetBytes(AccessToken);
        Assert.AreEqual(tokenBytes.Length + 5 + nonce.Length, reader.ReadInt32LittleEndian());
        Assert.AreEqual(TdsFedAuthLibrary.SecurityToken << 1, reader.ReadByte());
        Assert.AreEqual(tokenBytes.Length, reader.ReadInt32LittleEndian());
        reader.Skip(tokenBytes.Length);
        CollectionAssert.AreEqual(nonce, reader.ReadSpan(nonce.Length).ToArray());
    }

    [TestMethod]
    public void FederatedAuthenticationTokenMessageUsesSpecifiedFraming()
    {
        var nonce = new byte[TdsPreLogin.NonceLength];
        var message = TdsFedAuth.EncodeTokenMessage(
          new TdsFedAuthLogin(AccessToken, EchoFederatedAuthenticationRequired: true, nonce));

        var token = Encoding.Unicode.GetBytes(AccessToken);
        Assert.AreEqual(
          message.Length - sizeof(int),
          BinaryPrimitives.ReadInt32LittleEndian(message));
        Assert.AreEqual(
          token.Length,
          BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(sizeof(int))));
        CollectionAssert.AreEqual(token, message.AsSpan(8, token.Length).ToArray());
        Assert.AreEqual(8 + token.Length + nonce.Length, message.Length);
    }

    [TestMethod]
    public void FederatedAuthenticationTokenMessageOmitsAbsentNonce()
    {
        var message = TdsFedAuth.EncodeTokenMessage(
          new TdsFedAuthLogin(AccessToken, EchoFederatedAuthenticationRequired: false, default));

        Assert.AreEqual(8 + Encoding.Unicode.GetByteCount(AccessToken), message.Length);
    }

    [TestMethod]
    public void FedAuthInfoIsParsedFromOutOfOrderOptions()
    {
        var info = TdsFedAuth.ParseInfo(
          BuildFedAuthInfo("https://sts.example/authorize", "https://database.windows.net/"));

        Assert.AreEqual("https://sts.example/authorize", info.StsUrl);
        Assert.AreEqual("https://database.windows.net/", info.ServicePrincipalName);
    }

    [TestMethod]
    public void FedAuthInfoRejectsMissingServicePrincipalName()
    {
        var body = BuildFedAuthInfo("https://sts.example/authorize", spn: null);

        var error = Assert.ThrowsExactly<InvalidDataException>(() => TdsFedAuth.ParseInfo(body));
        StringAssert.Contains(error.Message, "STSURL or SPN");
    }

    [TestMethod]
    public void FedAuthInfoRejectsDataOutsideToken()
    {
        var body = BuildFedAuthInfo("https://sts.example/authorize", "spn");
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), (uint)body.Length);

        Assert.ThrowsExactly<InvalidDataException>(() => TdsFedAuth.ParseInfo(body));
    }

    [TestMethod]
    public void FedAuthInfoRejectsImpossibleOptionCount()
    {
        var body = BuildFedAuthInfo("https://sts.example/authorize", "spn");
        BinaryPrimitives.WriteUInt32LittleEndian(body, uint.MaxValue);

        Assert.ThrowsExactly<InvalidDataException>(() => TdsFedAuth.ParseInfo(body));
    }

    [TestMethod]
    public void FedAuthInfoRejectsOddLengthUnicodeData()
    {
        var body = BuildFedAuthInfo("sts", "spn");
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), 5);

        Assert.ThrowsExactly<InvalidDataException>(() => TdsFedAuth.ParseInfo(body));
    }

    [TestMethod]
    public void FeatureExtAckReportsFederatedAuthenticationAcknowledgement()
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteByte(TdsTokenType.FeatureExtAck);
        payload.WriteByte(TdsFeatureId.JsonSupport);
        payload.WriteInt32LittleEndian(1);
        payload.WriteByte(1);
        payload.WriteByte(TdsFeatureId.FedAuth);
        payload.WriteInt32LittleEndian(0);
        payload.WriteByte(TdsFeatureId.Terminator);
        payload.WriteByte(TdsTokenType.Done);

        TdsTokenReader reader = new(payload.WrittenMemory);
        Assert.AreEqual(TdsTokenType.FeatureExtAck, reader.ReadTokenType());
        var acknowledgement = reader.ReadFeatureExtAck();

        Assert.IsTrue(acknowledgement.FedAuthAcknowledged);
        Assert.AreEqual(0, acknowledgement.FedAuthDataLength);
        Assert.AreEqual(TdsTokenType.Done, reader.ReadTokenType());
    }

    [TestMethod]
    public void FeatureExtAckReportsUnexpectedFederatedAuthenticationData()
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteByte(TdsFeatureId.FedAuth);
        payload.WriteInt32LittleEndian(4);
        payload.WriteInt32LittleEndian(0);
        payload.WriteByte(TdsFeatureId.Terminator);

        TdsTokenReader reader = new(payload.WrittenMemory);

        Assert.AreEqual(4, reader.ReadFeatureExtAck().FedAuthDataLength);
    }

    [TestMethod]
    public void FedAuthInfoTokenIsReadWithItsDeclaredLength()
    {
        var body = BuildFedAuthInfo("https://sts.example/authorize", "spn");
        ArrayBufferWriter<byte> payload = new();
        payload.WriteUInt32LittleEndian((uint)body.Length);
        payload.Write(body);
        payload.WriteByte(TdsTokenType.Done);

        TdsTokenReader reader = new(payload.WrittenMemory);
        var info = reader.ReadFedAuthInfo();

        Assert.AreEqual("spn", info.ServicePrincipalName);
        Assert.AreEqual(TdsTokenType.Done, reader.ReadTokenType());
    }

    internal static byte[] BuildFedAuthInfo(string? stsUrl, string? spn)
    {
        List<(byte Id, byte[] Data)> options = [];
        if (spn is not null)
        {
            options.Add((0x02, Encoding.Unicode.GetBytes(spn)));
        }

        if (stsUrl is not null)
        {
            options.Add((0x01, Encoding.Unicode.GetBytes(stsUrl)));
        }

        ArrayBufferWriter<byte> body = new();
        body.WriteUInt32LittleEndian((uint)options.Count);
        var dataOffset = sizeof(uint) + (options.Count * 9);
        foreach ((var id, var data) in options)
        {
            body.WriteByte(id);
            body.WriteUInt32LittleEndian((uint)data.Length);
            body.WriteUInt32LittleEndian((uint)dataOffset);
            dataOffset += data.Length;
        }

        foreach ((_, var data) in options)
        {
            body.Write(data);
        }

        return body.WrittenMemory.ToArray();
    }

    internal static ReadOnlySpan<byte> ReadFeatureExtension(byte[] login)
    {
        int extensionPointer = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(56));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(login.AsSpan(extensionPointer));
        return login.AsSpan(offset);
    }

    private static byte[] BuildPreLoginResponse(byte federatedAuthentication, byte[]? nonce)
    {
        List<(byte Option, byte[] Data)> options =
        [
            (0x01, [(byte)TdsEncryptionLevel.On]),
            (0x06, [federatedAuthentication]),
        ];
        if (nonce is not null)
        {
            options.Add((0x07, nonce));
        }

        var tableLength = (options.Count * 5) + 1;
        ArrayBufferWriter<byte> payload = new();
        var dataOffset = tableLength;
        foreach ((var option, var data) in options)
        {
            payload.WriteByte(option);
            payload.WriteUInt16BigEndian((ushort)dataOffset);
            payload.WriteUInt16BigEndian((ushort)data.Length);
            dataOffset += data.Length;
        }

        payload.WriteByte(0xFF);
        foreach ((_, var data) in options)
        {
            payload.Write(data);
        }

        return payload.WrittenMemory.ToArray();
    }
}
