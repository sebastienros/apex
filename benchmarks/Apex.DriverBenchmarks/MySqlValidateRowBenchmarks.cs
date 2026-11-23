using System.Buffers.Binary;
using Apex.MySqlClient;
using Apex.MySqlClient.Internal;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

/// <summary>
/// Measures the per-row validation that the MySQL receive path runs on every row packet before
/// it is buffered, on the same 17-column shape as <see cref="MySqlHotPathBenchmarks"/>. The
/// <c>Decode*</c> cases there read already-buffered rows, so they never run this code. Needs no
/// database.
/// </summary>
[MemoryDiagnoser]
public class MySqlValidateRowBenchmarks
{
    private const int RowCount = 100;
    private static readonly System.Globalization.CultureInfo Invariant =
      System.Globalization.CultureInfo.InvariantCulture;

    private static readonly (string Name, MySqlType Type)[] s_shape =
    [
        ("id", MySqlType.Long),
        ("c_bigint", MySqlType.LongLong),
        ("c_small", MySqlType.Short),
        ("c_tiny", MySqlType.Tiny),
        ("c_double", MySqlType.Double),
        ("c_dec", MySqlType.NewDecimal),
        ("c_dec_wide", MySqlType.NewDecimal),
        ("c_vc1", MySqlType.VarString),
        ("c_vc2", MySqlType.VarString),
        ("c_text", MySqlType.Blob),
        ("c_dt", MySqlType.DateTime),
        ("c_date", MySqlType.Date),
        ("c_bool", MySqlType.Tiny),
        ("c_ubig", MySqlType.LongLong),
        ("c_float", MySqlType.Float),
        ("c_vc3", MySqlType.VarString),
        ("c_json", MySqlType.Json),
    ];

    private MySqlRowDecoder _text = null!;
    private MySqlRowDecoder _binary = null!;
    private byte[][] _textRows = [];
    private byte[][] _binaryRows = [];

    [GlobalSetup]
    public void Setup()
    {
        var metadata = s_shape
          .Select(column => new MySqlColumnMetadata(
            column.Name,
            column.Name,
            "apex_bench_wide",
            "apex_bench_wide",
            "apex",
            column.Type,
            MySqlColumnFlags.NotNull,
            CharacterSet: 255,
            ColumnLength: 64,
            Decimals: 0))
          .ToArray();
        _text = new MySqlRowDecoder(new Utf8StringCache(1024, 64), MySqlZeroDateBehavior.Error);
        _text.SetColumns(metadata, binary: false);
        _binary = new MySqlRowDecoder(new Utf8StringCache(1024, 64), MySqlZeroDateBehavior.Error);
        _binary.SetColumns(metadata, binary: true);
        _textRows = [.. Enumerable.Range(1, RowCount).Select(BuildTextRow)];
        _binaryRows = [.. Enumerable.Range(1, RowCount).Select(BuildBinaryRow)];
    }

    [Benchmark]
    public int ValidateText100Rows()
    {
        foreach (var row in _textRows)
        {
            _text.ValidateRow(row);
        }

        return _textRows.Length;
    }

    [Benchmark]
    public int ValidateBinary100Rows()
    {
        foreach (var row in _binaryRows)
        {
            _binary.ValidateRow(row);
        }

        return _binaryRows.Length;
    }

    private static string Json(int n) =>
      string.Create(Invariant, $$"""{"id": {{n}}, "name": "item-{{n}}", "tags": ["a", "b", "c"]}""");

    private static byte[] BuildTextRow(int n)
    {
        string[] values =
        [
            n.ToString(Invariant), (n * 1000003L).ToString(Invariant), (n % 30000).ToString(Invariant), (n % 100).ToString(Invariant),
            (n / 7.0).ToString("R", Invariant), (n * 12.3456m).ToString(Invariant), string.Create(Invariant, $"{n * 987654321L}.{n:D10}"),
            string.Create(Invariant, $"name-{n}"), string.Create(Invariant, $"a somewhat longer description {n}"), new string('x', 40),
            "2024-01-01 12:34:56.123456", "2024-01-02", (n % 2).ToString(Invariant),
            (n * 18446744073709L).ToString(Invariant), (n / 3.0f).ToString("R", Invariant), "tag", Json(n),
        ];
        List<byte> row = [];
        foreach (var value in values)
        {
            AddLengthEncoded(row, System.Text.Encoding.UTF8.GetBytes(value));
        }

        return [.. row];
    }

    private static byte[] BuildBinaryRow(int n)
    {
        List<byte> row = [0x00];
        row.AddRange(new byte[(s_shape.Length + 9) / 8]);
        var scratch = new byte[8];
        void Fixed(long value, int width)
        {
            BinaryPrimitives.WriteInt64LittleEndian(scratch, value);
            row.AddRange(scratch.AsSpan(0, width));
        }

        Fixed(n, 4);
        Fixed(n * 1000003L, 8);
        Fixed(n % 30000, 2);
        Fixed(n % 100, 1);
        Fixed(BitConverter.DoubleToInt64Bits(n / 7.0), 8);
        AddLengthEncoded(row, System.Text.Encoding.ASCII.GetBytes((n * 12.3456m).ToString(Invariant)));
        AddLengthEncoded(row, System.Text.Encoding.ASCII.GetBytes(string.Create(Invariant, $"{n * 987654321L}.{n:D10}")));
        AddLengthEncoded(row, System.Text.Encoding.UTF8.GetBytes(string.Create(Invariant, $"name-{n}")));
        AddLengthEncoded(row, System.Text.Encoding.UTF8.GetBytes(string.Create(Invariant, $"a somewhat longer description {n}")));
        AddLengthEncoded(row, System.Text.Encoding.UTF8.GetBytes(new string('x', 40)));
        row.AddRange([11, 0xE8, 0x07, 1, 1, 12, 34, 56, 0x40, 0xE2, 0x01, 0x00]);
        row.AddRange([4, 0xE8, 0x07, 1, 2]);
        Fixed(n % 2, 1);
        Fixed(n * 18446744073709L, 8);
        Fixed(BitConverter.SingleToInt32Bits(n / 3.0f), 4);
        AddLengthEncoded(row, "tag"u8.ToArray());
        AddLengthEncoded(row, System.Text.Encoding.UTF8.GetBytes(Json(n)));
        return [.. row];
    }

    private static void AddLengthEncoded(List<byte> row, byte[] value)
    {
        if (value.Length >= 251)
        {
            throw new InvalidOperationException("Benchmark values use one-byte length prefixes.");
        }

        row.Add((byte)value.Length);
        row.AddRange(value);
    }
}
