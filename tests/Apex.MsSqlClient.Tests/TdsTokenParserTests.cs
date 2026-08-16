using System.Buffers;
using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsTokenParserTests
{
    [TestMethod]
    public void ParsesResultChainsInfoAndEnvironmentChanges()
    {
        ArrayBufferWriter<byte> response = new();
        WriteInfo(response, TdsTokenType.Info, 5701, 10, "Changed database context.");
        WriteEnvironmentChange(response, TdsEnvironmentChange.PacketSize, "8192", "4096");
        WriteIntResult(response, "first", 1, more: true);
        WriteIntResult(response, "second", 2, more: false);

        List<MsSqlInfo> infos = [];
        List<TdsEnvironmentChangeInfo> changes = [];
        var parsed = new TdsQueryParser(new MsSqlRowDecoder()).Parse(
          response.WrittenMemory,
          infos.Add,
          changes.Add);

        Assert.AreEqual(1, infos.Count);
        Assert.AreEqual(5701, infos[0].Number);
        Assert.AreEqual(8192, changes[0].PacketSize);
        Assert.AreEqual(1, parsed.Rows[0].GetInt32(0));
        Assert.IsNotNull(parsed.Rows.Next);
        Assert.AreEqual(2, parsed.Rows.Next[0].GetInt32(0));
        Assert.IsTrue(parsed.IsFinal);
    }

    [TestMethod]
    public void ParsesStructuredServerError()
    {
        ArrayBufferWriter<byte> response = new();
        WriteInfo(response, TdsTokenType.Error, 208, 16, "Invalid object name 'missing'.");
        WriteDone(response, more: false);

        var parsed =
          new TdsQueryParser(new MsSqlRowDecoder()).Parse(response.WrittenMemory);

        Assert.IsNotNull(parsed.Error);
        Assert.AreEqual(208, parsed.Error.Number);
        Assert.AreEqual((byte)16, parsed.Error.Severity);
        Assert.AreEqual("procedure", parsed.Error.ProcedureName);
        Assert.AreEqual(7, parsed.Error.LineNumber);
    }

    [TestMethod]
    public void ReadsReturnValueMetadataAndValidatedIntHandle()
    {
        ArrayBufferWriter<byte> response = new();
        WriteReturnValue(response, 7, "@handle", 73);
        TdsTokenReader reader = new(response.WrittenMemory);

        Assert.AreEqual(TdsTokenType.ReturnValue, reader.ReadTokenType());
        var returnValue = reader.ReadReturnValue();

        Assert.AreEqual((ushort)7, returnValue.Ordinal);
        Assert.AreEqual("@handle", returnValue.Name);
        Assert.AreEqual((byte)1, returnValue.Status);
        Assert.AreEqual(0x01020304u, returnValue.UserType);
        Assert.AreEqual((ushort)0x0506, returnValue.Flags);
        Assert.AreEqual(TdsDataType.IntN, returnValue.TypeInfo.Type);
        Assert.AreEqual(sizeof(int), returnValue.TypeInfo.MaximumLength);
        Assert.AreEqual(73, returnValue.GetPreparedHandle());
        Assert.IsFalse(reader.HasRemaining);
    }

    [TestMethod]
    public void RejectsNullPreparedHandle()
    {
        ArrayBufferWriter<byte> response = new();
        WriteReturnValue(response, 1, string.Empty, handle: null);
        TdsTokenReader reader = new(response.WrittenMemory);
        _ = reader.ReadTokenType();
        var returnValue = reader.ReadReturnValue();

        Assert.ThrowsExactly<InvalidDataException>(
          () => _ = returnValue.GetPreparedHandle());
    }

    private static void WriteIntResult(
        ArrayBufferWriter<byte> response,
        string name,
        int value,
        bool more)
    {
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar(name);
        response.WriteByte(TdsTokenType.Row);
        response.WriteInt32LittleEndian(value);
        WriteDone(response, more);
    }

    private static void WriteInfo(
        ArrayBufferWriter<byte> response,
        byte token,
        int number,
        byte severity,
        string message)
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteInt32LittleEndian(number);
        body.WriteByte(1);
        body.WriteByte(severity);
        body.WriteUInt16LittleEndian(checked((ushort)message.Length));
        body.WriteUtf16(message);
        body.WriteBVarChar("server");
        body.WriteBVarChar("procedure");
        body.WriteInt32LittleEndian(7);
        response.WriteByte(token);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
    }

    private static void WriteEnvironmentChange(
        ArrayBufferWriter<byte> response,
        byte type,
        string newValue,
        string oldValue)
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteByte(type);
        body.WriteBVarChar(newValue);
        body.WriteBVarChar(oldValue);
        response.WriteByte(TdsTokenType.EnvironmentChange);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
    }

    private static void WriteReturnValue(
        ArrayBufferWriter<byte> response,
        ushort ordinal,
        string name,
        int? handle)
    {
        response.WriteByte(TdsTokenType.ReturnValue);
        response.WriteUInt16LittleEndian(ordinal);
        response.WriteBVarChar(name);
        response.WriteByte(1);
        response.WriteUInt32LittleEndian(0x01020304);
        response.WriteUInt16LittleEndian(0x0506);
        response.WriteByte(TdsDataType.IntN);
        response.WriteByte(sizeof(int));
        if (handle is int value)
        {
            response.WriteByte(sizeof(int));
            response.WriteInt32LittleEndian(value);
        }
        else
        {
            response.WriteByte(0);
        }
    }

    private static void WriteDone(ArrayBufferWriter<byte> response, bool more)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian(more ? (ushort)TdsDoneStatus.More : (ushort)0);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }
}
