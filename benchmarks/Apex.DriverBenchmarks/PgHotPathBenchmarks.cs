using System.Buffers.Binary;
using Apex.PgClient;
using Apex.PgClient.Internal;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

// Database-free PostgreSQL hot paths: generic typed reads, numeric to decimal, and Bind
// message construction. These isolate driver CPU and allocation costs from network noise.
[MemoryDiagnoser]
public class PgHotPathBenchmarks
{
    private const int ColumnCount = 10;
    private readonly PgRowDecoder _rowDecoder = new(0, 0);
    private readonly SqlColumn _int32Column = new("value", 23, 4, -1, SqlDataFormat.Binary);
    private readonly SqlColumn _guidColumn = new("value", 2950, 16, -1, SqlDataFormat.Binary);
    private readonly SqlColumn _numericColumn = new("value", 1700, -1, -1, SqlDataFormat.Binary);
    private readonly byte[] _int32Row = CreateInt32Row();
    private readonly byte[] _guidRow = CreateGuidRow();

    // 12345.6789: weight 1, digits [1, 2345, 6789], dscale 4.
    private readonly byte[] _numericSmall = Numeric(1, 0, 4, 1, 2345, 6789);

    // 1234.50 (money-like, keeps the trailing zero): weight 0, digits [1234, 5000], dscale 2.
    private readonly byte[] _numericMoney = Numeric(0, 0, 2, 1234, 5000);

    // -1234567890123456.789012: weight 3, dscale 6, negative.
    private readonly byte[] _numericLarge =
      Numeric(3, 0x4000, 6, 1234, 5678, 9012, 3456, 7890, 1200);

    // 123456789012345678.901234: 24 significant digits, weight 4, dscale 6.
    private readonly byte[] _numericWide =
      Numeric(4, 0, 6, 12, 3456, 7890, 1234, 5678, 9012, 3400);

    private readonly byte[] _numericSmallRow;
    private readonly PgWireWriter _writer = new(Stream.Null, new PgTypeRegistry());
    private readonly SqlParameters _int32Parameter = SqlParameters.Create(42);
    private readonly SqlParameters _mixedParameters =
      SqlParameters.Create(42, 1234567890123L, 3.14159, "hello world", 12.5m, true);

    public PgHotPathBenchmarks()
    {
        _numericSmallRow = CreateRow(_numericSmall);
    }

    [Benchmark]
    public int GetInt32FirstOrdinal() =>
      _rowDecoder.Decode<int>(_int32Row, 0, _int32Column, false);

    [Benchmark]
    public int GetInt32LastOrdinal() =>
      _rowDecoder.Decode<int>(_int32Row, ColumnCount - 1, _int32Column, false);

    [Benchmark]
    public Guid GetGuidLastOrdinal() =>
      _rowDecoder.Decode<Guid>(_guidRow, ColumnCount - 1, _guidColumn, false);

    [Benchmark]
    public decimal GetDecimal() =>
      _rowDecoder.Decode<decimal>(_numericSmallRow, 0, _numericColumn, false);

    [Benchmark]
    public decimal DecodeDecimalSmall() => PgBinaryCodec.DecodeDecimal(_numericSmall);

    [Benchmark]
    public decimal DecodeDecimalMoney() => PgBinaryCodec.DecodeDecimal(_numericMoney);

    [Benchmark]
    public decimal DecodeDecimalLarge() => PgBinaryCodec.DecodeDecimal(_numericLarge);

    [Benchmark]
    public decimal DecodeDecimalWide() => PgBinaryCodec.DecodeDecimal(_numericWide);

    [Benchmark]
    public ValueTask WritePreparedBindInt32() =>
      _writer.WritePreparedQueryAsync("s1", _int32Parameter, CancellationToken.None, describePortal: false);

    [Benchmark]
    public ValueTask WritePreparedBindMixed() =>
      _writer.WritePreparedQueryAsync("s1", _mixedParameters, CancellationToken.None, describePortal: false);

    [Benchmark]
    public ValueTask WriteUnpreparedQueryInt32() =>
      _writer.WriteExtendedQueryAsync("SELECT $1::int4", _int32Parameter, CancellationToken.None);

    private static byte[] Numeric(short weight, ushort sign, short dscale, params short[] digits)
    {
        var binary = new byte[(4 + digits.Length) * sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(binary, (short)digits.Length);
        BinaryPrimitives.WriteInt16BigEndian(binary.AsSpan(2), weight);
        BinaryPrimitives.WriteUInt16BigEndian(binary.AsSpan(4), sign);
        BinaryPrimitives.WriteInt16BigEndian(binary.AsSpan(6), dscale);
        for (var i = 0; i < digits.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(binary.AsSpan(8 + (i * 2)), digits[i]);
        }

        return binary;
    }

    private static byte[] CreateInt32Row()
    {
        var values = new byte[ColumnCount][];
        for (var i = 0; i < ColumnCount; i++)
        {
            values[i] = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(values[i], i + 1);
        }

        return CreateRow(values);
    }

    private static byte[] CreateGuidRow()
    {
        var values = new byte[ColumnCount][];
        for (var i = 0; i < ColumnCount; i++)
        {
            values[i] = Guid.Parse("12345678-1234-5678-9012-123456789abc").ToByteArray(bigEndian: true);
        }

        return CreateRow(values);
    }

    private static byte[] CreateRow(params byte[][] values)
    {
        var length = sizeof(short);
        foreach (var value in values)
        {
            length += sizeof(int) + value.Length;
        }

        var row = new byte[length];
        BinaryPrimitives.WriteInt16BigEndian(row, (short)values.Length);
        var position = sizeof(short);
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(position), value.Length);
            position += sizeof(int);
            value.CopyTo(row, position);
            position += value.Length;
        }

        return row;
    }
}
