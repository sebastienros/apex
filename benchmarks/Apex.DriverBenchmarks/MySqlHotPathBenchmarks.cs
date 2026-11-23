using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using MySqlConnector;
using ApexMySql = Apex.MySqlClient;

namespace Apex.DriverBenchmarks;

/// <summary>
/// Measures Apex.MySqlClient hot paths on a 16-column mixed-type table: per-result column
/// metadata handling (single-row queries), reading every column of every row (field access),
/// and DECIMAL decoding. The <c>Decode*</c> cases read already-buffered rows, so they measure
/// decoding without the network round trip.
/// </summary>
[MemoryDiagnoser]
public class MySqlHotPathBenchmarks
{
    private const string Projection =
      "id, c_bigint, c_small, c_tiny, c_double, c_dec, c_dec_wide, c_vc1, c_vc2, c_text, " +
      "c_dt, c_date, c_bool, c_ubig, c_float, c_vc3, c_json";
    private const string WideSql = "SELECT " + Projection + " FROM apex_bench_wide ORDER BY id";
    private const string WideByIdSql = "SELECT " + Projection + " FROM apex_bench_wide WHERE id = ?";
    private const string WideFromIdSql =
      "SELECT " + Projection + " FROM apex_bench_wide WHERE id >= ? ORDER BY id";

    private ApexMySql.MySqlConnection _apex = null!;
    private ISqlPreparedStatement _byId = null!;
    private ISqlPreparedStatement _fromId = null!;
    private ISqlPreparedStatement _decimalGuid = null!;
    private SqlRow[] _textRows = [];
    private SqlRow[] _binaryRows = [];

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("APEX_MYSQL_CONNECTION_STRING") ??
            throw new InvalidOperationException(
                "Set APEX_MYSQL_CONNECTION_STRING before running database benchmarks.");
        MySqlConnectionStringBuilder builder = new(connectionString);
        await using (MySqlConnection setup = new(builder.ConnectionString))
        {
            await setup.OpenAsync();
            await using MySqlCommand command = new(
              """
              DROP TABLE IF EXISTS apex_bench_wide;
              CREATE TABLE apex_bench_wide (
                id INT NOT NULL PRIMARY KEY,
                c_bigint BIGINT NOT NULL,
                c_small SMALLINT NOT NULL,
                c_tiny TINYINT NOT NULL,
                c_double DOUBLE NOT NULL,
                c_dec DECIMAL(18,4) NOT NULL,
                c_dec_wide DECIMAL(30,10) NOT NULL,
                c_vc1 VARCHAR(32) NOT NULL,
                c_vc2 VARCHAR(64) NOT NULL,
                c_text TEXT NOT NULL,
                c_dt DATETIME(6) NOT NULL,
                c_date DATE NOT NULL,
                c_bool BOOLEAN NOT NULL,
                c_ubig BIGINT UNSIGNED NOT NULL,
                c_float FLOAT NOT NULL,
                c_vc3 VARCHAR(16) NOT NULL,
                c_json JSON NOT NULL);
              INSERT INTO apex_bench_wide
              WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 100)
              SELECT n, n * 1000003, n % 30000, n % 100, n / 7.0, n * 12.3456,
                CAST(CONCAT(n * 987654321, '.', LPAD(n, 10, '0')) AS DECIMAL(30,10)),
                CONCAT('name-', n), CONCAT('a somewhat longer description ', n), REPEAT('x', 40),
                TIMESTAMP('2024-01-01 12:34:56.123456') + INTERVAL n MINUTE,
                DATE('2024-01-01') + INTERVAL n DAY, n % 2, n * 18446744073709, n / 3.0, 'tag',
                JSON_OBJECT('id', n, 'name', CONCAT('item-', n), 'tags', JSON_ARRAY('a', 'b', 'c'))
              FROM seq;
              """,
              setup);
            await command.ExecuteNonQueryAsync();
        }

        _apex = await ApexMySql.MySqlClient.ConnectAsync(new ApexMySql.MySqlConnectOptions
        {
            Host = builder.Server,
            Port = (int)builder.Port,
            Database = builder.Database,
            Username = builder.UserID,
            Password = builder.Password,
        });
        _byId = await _apex.PrepareAsync(WideByIdSql);
        _fromId = await _apex.PrepareAsync(WideFromIdSql);
        _decimalGuid = await _apex.PrepareAsync("SELECT LENGTH(?) + LENGTH(?)");
        _textRows = [.. await _apex.QueryAsync(WideSql)];
        _binaryRows = [.. await _fromId.QueryAsync(SqlParameters.Create(1))];
        if (_textRows.Length != 100 || _binaryRows.Length != 100)
        {
            throw new InvalidOperationException("Unexpected benchmark row count.");
        }

        // Warm the string caches so the measured iterations see steady-state behavior.
        _ = ReadAll(_textRows);
        _ = ReadAll(_binaryRows);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _byId.DisposeAsync();
        await _fromId.DisposeAsync();
        await _decimalGuid.DisposeAsync();
        await _apex.DisposeAsync();
    }

    [Benchmark]
    public async Task<long> QueryWideSingleRowText()
    {
        var rows = await _apex.QueryAsync("SELECT " + Projection + " FROM apex_bench_wide WHERE id = 7");
        return ReadAll(rows);
    }

    [Benchmark]
    public async Task<long> QueryWideSingleRowPrepared()
    {
        var rows = await _byId.QueryAsync(SqlParameters.Create(7));
        return ReadAll(rows);
    }

    [Benchmark]
    public async Task<long> QueryWide100RowsText()
    {
        var rows = await _apex.QueryAsync(WideSql);
        return ReadAll(rows);
    }

    [Benchmark]
    public async Task<long> QueryWide100RowsPrepared()
    {
        var rows = await _fromId.QueryAsync(SqlParameters.Create(1));
        return ReadAll(rows);
    }

    [Benchmark]
    public long DecodeWide100RowsText() => ReadAll(_textRows);

    [Benchmark]
    public long DecodeWide100RowsBinary() => ReadAll(_binaryRows);

    [Benchmark]
    public long DecodeLastColumn100RowsText()
    {
        long total = 0;
        foreach (var row in _textRows)
        {
            total += row.GetString(15).Length;
        }

        return total;
    }

    [Benchmark]
    public long DecodeLastColumn100RowsBinary()
    {
        long total = 0;
        foreach (var row in _binaryRows)
        {
            total += row.GetString(15).Length;
        }

        return total;
    }

    [Benchmark]
    public int DecodeMySqlDecimal100RowsText()
    {
        var total = 0;
        foreach (var row in _textRows)
        {
            total += row.Get<ApexMySql.MySqlDecimal>(6).Scale;
        }

        return total;
    }

    [Benchmark]
    public int DecodeMySqlDecimal100RowsBinary()
    {
        var total = 0;
        foreach (var row in _binaryRows)
        {
            total += row.Get<ApexMySql.MySqlDecimal>(6).Scale;
        }

        return total;
    }

    [Benchmark]
    public int DecodeJson100RowsText()
    {
        var total = 0;
        foreach (var row in _textRows)
        {
            total += row.Get<System.Text.Json.JsonElement>(16).GetProperty("tags").GetArrayLength();
        }

        return total;
    }

    [Benchmark]
    public async Task<long> PreparedDecimalGuidParameters()
    {
        var rows = await _decimalGuid.QueryAsync(
          SqlParameters.Create(123456.7890m, s_guid));
        return rows[0].GetInt64(0);
    }

    private static readonly Guid s_guid = Guid.Parse("12345678-1234-5678-9012-123456789abc");

    private static long ReadAll(IEnumerable<SqlRow> rows)
    {
        long total = 0;
        foreach (var row in rows)
        {
            total += row.GetInt32(0);
            total += row.GetInt64(1);
            total += row.GetInt16(2);
            total += row.Get<sbyte>(3);
            total += (long)row.GetDouble(4);
            total += (long)row.GetDecimal(5);
            total += (long)row.GetDecimal(6);
            total += row.GetString(7).Length;
            total += row.GetString(8).Length;
            total += row.GetString(9).Length;
            total += row.GetDateTime(10).Minute;
            total += row.GetDateOnly(11).Day;
            total += row.GetBoolean(12) ? 1 : 0;
            total += (long)(row.Get<ulong>(13) & 0xFFFF);
            total += (long)row.GetFloat(14);
            total += row.GetString(15).Length;
        }

        return total;
    }
}
