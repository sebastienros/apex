using System.Buffers.Binary;
using System.Text;
using Apex.PgClient.Internal;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
public class CodecBenchmarks
{
    private readonly byte[] _numericText =
      Encoding.UTF8.GetBytes("123456789012345678901234567890.123456");
    private readonly byte[] _arrayText =
      Encoding.UTF8.GetBytes("""{"one,two",NULL,"three"}""");
    private readonly byte[] _numericBinary = CreateNumericBinary();
    private readonly PgRowDecoder _rowDecoder = new(0, 0);
    private readonly byte[] _int32Row = CreateRow(Int32(42));
    private readonly byte[] _guidRow = CreateRow(
      Guid.Parse("12345678-1234-5678-9012-123456789abc")
        .ToByteArray(bigEndian: true));
    private readonly SqlColumn _int32Column =
      new("value", 23, 4, -1, SqlDataFormat.Binary);
    private readonly SqlColumn _guidColumn =
      new("value", 2950, 16, -1, SqlDataFormat.Binary);

    [Benchmark(Baseline = true)]
    public object DecodeNumericText() => PgTextCodec.Decode(1700, _numericText);

    [Benchmark]
    public object DecodeNumericBinary() => PgBinaryCodec.Decode(1700, _numericBinary);

    [Benchmark]
    public object DecodeTextArray() => PgTextCodec.Decode(1009, _arrayText);

    [Benchmark]
    public int DecodeTypedInt32() =>
      _rowDecoder.DecodeInt32(_int32Row, 0, _int32Column);

    [Benchmark]
    public Guid DecodeTypedGuid() =>
      _rowDecoder.DecodeGuid(_guidRow, 0, _guidColumn);

    private static byte[] CreateNumericBinary()
    {
        short[] values = [5, 2, 0, 6, 12, 3456, 7890, 1234, 5678];
        var binary = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(binary.AsSpan(i * 2), values[i]);
        }

        return binary;
    }

    private static byte[] CreateRow(ReadOnlySpan<byte> value)
    {
        var row =
          new byte[sizeof(short) + sizeof(int) + value.Length];
        BinaryPrimitives.WriteInt16BigEndian(row, 1);
        BinaryPrimitives.WriteInt32BigEndian(
          row.AsSpan(sizeof(short)),
          value.Length);
        value.CopyTo(
          row.AsSpan(sizeof(short) + sizeof(int)));
        return row;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
