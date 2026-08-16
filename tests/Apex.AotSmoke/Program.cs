using System.Text.Json;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient;

PgConnectOptions pgOptions = PgConnectOptions.Parse(
  "host=localhost port=5432 user=user dbname=db sslmode=disable");
MySqlConnectOptions mySqlOptions = new()
{
    Host = "localhost",
    Port = 3306,
    Database = "db",
    Username = "user",
};
MsSqlConnectOptions msSqlOptions = MsSqlConnectOptions.Parse(
  "Server=tcp:localhost,1433;Database=db;User ID=user;Encrypt=Strict;" +
  "TrustServerCertificate=false;Application Name=aot-smoke");
Func<PgConnectOptions, CancellationToken, ValueTask<PgConnection>> pgConnect =
  PgClient.ConnectAsync;
Func<MySqlConnectOptions, CancellationToken, ValueTask<MySqlConnection>> mySqlConnect =
  MySqlClient.ConnectAsync;
Func<MsSqlConnectOptions, CancellationToken, ValueTask<MsSqlConnection>> msSqlConnect =
  MsSqlClient.ConnectAsync;
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
if (System.Text.Encoding.GetEncoding(1252).GetString([0x80]) != "€")
{
    throw new InvalidOperationException("NativeAOT code-page decoding failed.");
}
using JsonDocument json = JsonDocument.Parse("""{"aot":true}""");
SqlParameters parameters = SqlParameters.Create(
  SqlValue.Null,
  true,
  (short)7,
  42,
  84L,
  1.5f,
  2.5d,
  12.34m,
  "value",
  new byte[] { 1, 2, 3 },
  Guid.Empty,
  new DateOnly(2026, 8, 14),
  new TimeOnly(18, 11, 14),
  new DateTime(2026, 8, 14, 18, 11, 14, DateTimeKind.Unspecified),
  new DateTimeOffset(2026, 8, 14, 18, 11, 14, TimeSpan.Zero),
  json.RootElement.Clone());
Console.WriteLine(
  $"pg={pgOptions.Host}:{pgOptions.Port}/{pgOptions.Database} " +
  $"mysql={mySqlOptions.Host}:{mySqlOptions.Port}/{mySqlOptions.Database} " +
  $"mssql={msSqlOptions.Host}:{msSqlOptions.Port}/{msSqlOptions.Database} " +
  $"encryption={msSqlOptions.EncryptionMode} parameters={parameters.Count}");
GC.KeepAlive(pgConnect);
GC.KeepAlive(mySqlConnect);
GC.KeepAlive(msSqlConnect);

if (Environment.GetEnvironmentVariable("APEX_AOT_MYSQL_CONNECTION_STRING") is { Length: > 0 } connectionString)
{
    await using var connection = await MySqlClient.ConnectAsync(connectionString);
    await connection.PingAsync();
    var rows = await connection.QueryAsync("SELECT 1 AS value, 'aot' AS label");
    if (rows[0].GetInt32("value") != 1 || rows[0].GetString("label") != "aot")
    {
        throw new InvalidOperationException("The NativeAOT MySQL text query returned unexpected values.");
    }

    await using var statement =
      await connection.PrepareAsync("SELECT ? + 1 AS value");
    rows = await statement.QueryAsync(SqlParameters.Create(41));
    if (rows[0].GetInt64("value") != 42)
    {
        throw new InvalidOperationException(
          "The NativeAOT MySQL prepared query returned an unexpected value.");
    }

    await connection.ExecuteAsync(
      "CREATE TEMPORARY TABLE apex_aot_values (" +
      "id BIGINT AUTO_INCREMENT PRIMARY KEY, value INT NOT NULL)");
    var inserted = await connection.ExecuteAsync(
      "INSERT INTO apex_aot_values(value) VALUES (?)",
      SqlParameters.Create(7));
    if (inserted.AffectedRows != 1 || inserted.LastInsertId != 1)
    {
        throw new InvalidOperationException("The NativeAOT MySQL command metadata is invalid.");
    }

    await using (var transaction = await connection.BeginTransactionAsync())
    {
        await connection.ExecuteAsync("INSERT INTO apex_aot_values(value) VALUES (8)");
    }

    rows = await connection.QueryAsync("SELECT COUNT(*) AS count FROM apex_aot_values");
    if (rows[0].GetInt64("count") != 1)
    {
        throw new InvalidOperationException("The NativeAOT MySQL transaction was not rolled back.");
    }

    var readerSum = 0;
    await using (var reader =
                 await connection.ExecuteReaderAsync("SELECT 1 UNION ALL SELECT 2"))
    {
        while (await reader.ReadAsync())
        {
            readerSum += reader.GetInt32(0);
        }
    }

    var streamSum = 0;
    await foreach (var row in connection.StreamAsync(
                     "SELECT 1 UNION ALL SELECT 2",
                     fetchSize: 1))
    {
        streamSum += row.GetInt32(0);
    }

    if (readerSum != 3 || streamSum != 3)
    {
        throw new InvalidOperationException("The NativeAOT MySQL streaming results are invalid.");
    }

    using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200)))
    {
        try
        {
            await connection.QueryAsync("SELECT SLEEP(10)", cancellation.Token);
            throw new InvalidOperationException("The NativeAOT MySQL cancellation did not interrupt the query.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    rows = await connection.QueryAsync("SELECT 9");
    if (rows[0].GetInt32(0) != 9)
    {
        throw new InvalidOperationException(
          "The NativeAOT MySQL connection was not reusable after cancellation.");
    }

    await using MySqlPool pool = MySqlPool.Create(
      connectionString,
      new SqlPoolOptions { MaximumSize = 2 });
    rows = await pool.QueryAsync("SELECT 10");
    if (rows[0].GetInt32(0) != 10)
    {
        throw new InvalidOperationException("The NativeAOT MySQL pool returned an unexpected value.");
    }
}
