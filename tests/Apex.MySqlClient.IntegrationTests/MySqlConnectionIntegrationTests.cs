using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text.Json;
using Apex.SqlClient;
using Testcontainers.MySql;

namespace Apex.MySqlClient.IntegrationTests;

/// <summary>
/// Exercises <see cref="MySqlConnection"/> and <see cref="MySqlPool"/> against a real MySQL or
/// MariaDB server. One container is shared for every test in this class (started once in
/// <see cref="StartMySqlAsync"/>) so the suite does not pay container startup cost per test; each
/// test opens its own connection or pool so tests remain independent and parallel-safe.
/// </summary>
[TestClass]
public sealed class MySqlConnectionIntegrationTests
{
    private static MySqlContainer s_container = null!;

    [ClassInitialize]
    public static async Task StartMySqlAsync(TestContext testContext) =>
      s_container = await MySqlContainerFixture.StartAsync();

    [ClassCleanup]
    public static async Task StopMySqlAsync() => await s_container.DisposeAsync();

    private static MySqlConnectOptions Options => MySqlContainerFixture.CreateOptions(s_container);

    [TestMethod]
    public async Task ConnectsAndExecutesSimpleTextQuery()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);

        var rows = await connection.QueryAsync("SELECT 1 AS id, 'hello' AS message");

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(1, rows[0].Get<int>("id"));
        Assert.AreEqual("hello", rows[0].Get<string>("message"));
    }

    [TestMethod]
    public async Task UsesSupportedAuthenticationAndTlsModes()
    {
        var isMariaDb = MySqlContainerFixture.ResolveImage()
          .Contains("mariadb", StringComparison.OrdinalIgnoreCase);
        var options = isMariaDb
          ? Options with
          {
              AuthenticationPlugin = MySqlAuthenticationPlugin.NativePassword,
          }
          : Options with
          {
              SslMode = MySqlSslMode.Required,
              AllowPublicKeyRetrieval = false,
          };

        await using var connection = await MySqlClient.ConnectAsync(options);
        var rows = await connection.QueryAsync("SELECT CURRENT_USER()");

        StringAssert.StartsWith(rows[0].GetString(0), MySqlContainerFixture.Username + "@");
        if (!isMariaDb)
        {
            Assert.IsTrue(connection.IsSecure);
        }
    }

    [TestMethod]
    public async Task ConnectsQueriesAndBatchesOverUnixDomainSocket()
    {
        await using UnixSocketForwarder forwarder = new(Options.Host, Options.Port);
        var parsed = MySqlConnectOptions.Parse(
          $"Server=ignored;Unix Socket={forwarder.SocketPath};Database={Options.Database};" +
          $"User ID={Options.Username};Password={Options.Password};SslMode=Disabled");

        await using var connection = await MySqlClient.ConnectAsync(parsed);
        Assert.IsFalse(connection.IsSecure);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].GetInt32(0));
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE unix_batch (value INT)");
        await using var statement = await connection.PrepareAsync(
          "INSERT INTO unix_batch VALUES (?)");
        var results = await statement.ExecuteBatchAsync(
          [SqlParameters.Create(1), SqlParameters.Create(2)]);

        Assert.HasCount(2, results);
        Assert.AreEqual(
          2L,
          (await connection.QueryAsync("SELECT COUNT(*) FROM unix_batch"))[0].GetInt64(0));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
          () => MySqlClient.ConnectAsync(
            parsed with { SslMode = MySqlSslMode.Required }).AsTask());
    }

    [TestMethod]
    public async Task RejectsInvalidDatabaseUsernameAndPassword()
    {
        var database = await Assert.ThrowsExactlyAsync<MySqlException>(
          () => MySqlClient.ConnectAsync(
            Options with { Database = "missing_database" }).AsTask());
        var username = await Assert.ThrowsExactlyAsync<MySqlException>(
          () => MySqlClient.ConnectAsync(
            Options with { Username = "missing_user" }).AsTask());
        var password = await Assert.ThrowsExactlyAsync<MySqlException>(
          () => MySqlClient.ConnectAsync(
            Options with { Password = "wrong_password" }).AsTask());

        Assert.IsTrue(database.ErrorNumber is 1044 or 1049);
        Assert.AreEqual(1045, username.ErrorNumber);
        Assert.AreEqual(1045, password.ErrorNumber);
    }

      [TestMethod]
      public async Task ExhaustsConfiguredReconnectAttempts()
      {
        var port = ReserveUnusedPort();
        var options = Options with
        {
          Host = "127.0.0.1",
          Port = port,
          ConnectTimeout = TimeSpan.FromMilliseconds(100),
          ReconnectAttempts = 2,
          ReconnectInterval = TimeSpan.FromMilliseconds(100),
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(
          () => MySqlClient.ConnectAsync(options).AsTask());

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180), stopwatch.Elapsed);
      }

    [TestMethod]
    [DoNotParallelize]
    public async Task CachingSha2FullAuthenticationRequiresAnExplicitSecurePath()
    {
        if (MySqlContainerFixture.ResolveImage()
              .Contains("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using (var secure = await MySqlClient.ConnectAsync(
                       Options with { SslMode = MySqlSslMode.Required }))
        {
            await secure.ExecuteAsync(
              $"ALTER USER CURRENT_USER() IDENTIFIED BY '{MySqlContainerFixture.Password}'");
        }

        await Assert.ThrowsExactlyAsync<System.Security.Authentication.AuthenticationException>(
          () => MySqlClient.ConnectAsync(
              Options with
              {
                  SslMode = MySqlSslMode.Disabled,
                  AllowPublicKeyRetrieval = false,
              })
            .AsTask());

        await using var rsa = await MySqlClient.ConnectAsync(
          Options with
          {
              SslMode = MySqlSslMode.Disabled,
              AllowPublicKeyRetrieval = true,
          });
        var rows = await rsa.QueryAsync("SELECT 1");

        Assert.IsFalse(rsa.IsSecure);
        Assert.AreEqual(1, rows[0].GetInt32(0));
    }

    [TestMethod]
    public async Task UploadsLocalInfileWhenExplicitlyEnabled()
    {
        var fileName = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(fileName, "1,alpha\n2,beta\n");
            await using var connection = await MySqlClient.ConnectAsync(
              Options with { AllowLoadLocalInfile = true });
            await connection.ExecuteAsync(
              "CREATE TEMPORARY TABLE local_infile_probe (id INT, value VARCHAR(16))");
            var escaped = fileName
              .Replace("\\", "\\\\", StringComparison.Ordinal)
              .Replace("'", "\\'", StringComparison.Ordinal);

            var loaded = await connection.ExecuteAsync(
              $"LOAD DATA LOCAL INFILE '{escaped}' INTO TABLE local_infile_probe " +
              "FIELDS TERMINATED BY ',' LINES TERMINATED BY '\\n'");
            var rows = await connection.QueryAsync(
              "SELECT id, value FROM local_infile_probe ORDER BY id");

            Assert.AreEqual(2L, loaded.AffectedRows);
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("alpha", rows[0].GetString("value"));
            Assert.AreEqual("beta", rows[1].GetString("value"));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UploadsEmptyAndMultiPacketLocalInfiles()
    {
        var emptyFile = Path.GetTempFileName();
        var largeFile = Path.GetTempFileName();
        const int payloadLength = (16 * 1024 * 1024) + 1;
        try
        {
            var payload = GC.AllocateUninitializedArray<byte>(payloadLength + 1);
            payload.AsSpan(0, payloadLength).Fill((byte)'x');
            payload[^1] = (byte)'\n';
            await File.WriteAllBytesAsync(largeFile, payload);
            await using var connection = await MySqlClient.ConnectAsync(
              Options with { AllowLoadLocalInfile = true });
            await connection.ExecuteAsync(
              "CREATE TEMPORARY TABLE local_infile_boundary_probe (payload LONGBLOB)");

            var empty = await connection.ExecuteAsync(
              $"LOAD DATA LOCAL INFILE '{EscapeLocalInfilePath(emptyFile)}' " +
              "INTO TABLE local_infile_boundary_probe LINES TERMINATED BY '\\n'");
            var large = await connection.ExecuteAsync(
              $"LOAD DATA LOCAL INFILE '{EscapeLocalInfilePath(largeFile)}' " +
              "INTO TABLE local_infile_boundary_probe LINES TERMINATED BY '\\n'");
            var rows = await connection.QueryAsync(
              "SELECT OCTET_LENGTH(payload) AS payload_length " +
              "FROM local_infile_boundary_probe");

            Assert.AreEqual(0L, empty.AffectedRows);
            Assert.AreEqual(1L, large.AffectedRows);
            Assert.HasCount(1, rows);
            Assert.AreEqual(payloadLength, rows[0].GetInt32("payload_length"));
        }
        finally
        {
            File.Delete(emptyFile);
            File.Delete(largeFile);
        }
    }

    [TestMethod]
    public async Task PreparedLocalInfileBatchRemainsProtocolSynchronized()
    {
        if (!MySqlContainerFixture.ResolveImage()
              .Contains("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(fileName, "1,alpha\n2,beta\n");
            await using var connection = await MySqlClient.ConnectAsync(
              Options with
              {
                  AllowLoadLocalInfile = true,
                  PipeliningLimit = 8,
              });
            await connection.ExecuteAsync(
              "CREATE TEMPORARY TABLE prepared_local_infile_probe (id INT, value VARCHAR(16))");
            var escaped = fileName
              .Replace("\\", "\\\\", StringComparison.Ordinal)
              .Replace("'", "\\'", StringComparison.Ordinal);
            await using var statement = await connection.PrepareAsync(
              $"LOAD DATA LOCAL INFILE '{escaped}' INTO TABLE prepared_local_infile_probe " +
              "FIELDS TERMINATED BY ',' LINES TERMINATED BY '\\n'");

            var loaded = await statement.ExecuteBatchAsync(
              Enumerable.Repeat(SqlParameters.Empty, 8).ToArray());
            var rows = await connection.QueryAsync(
              "SELECT COUNT(*) AS count FROM prepared_local_infile_probe");

            Assert.AreEqual(8, loaded.Count);
            Assert.IsTrue(loaded.All(static result => result.AffectedRows == 2));
            Assert.AreEqual(16L, rows[0].GetInt64("count"));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [TestMethod]
    public async Task ReportsServerMetadataAndMariaDbCompatibilityPrefix()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);

        var imageIsMariaDb = MySqlContainerFixture.ResolveImage()
          .Contains("mariadb", StringComparison.OrdinalIgnoreCase);

        Assert.AreEqual(imageIsMariaDb, connection.ServerVersion.IsMariaDb);
        Assert.AreEqual(imageIsMariaDb ? "MariaDB" : "MySQL", connection.DatabaseMetadata.ProductName);
        Assert.IsGreaterThanOrEqualTo(5, connection.DatabaseMetadata.MajorVersion);
        Assert.IsTrue(connection.ConnectionId > 0);
    }

    [TestMethod]
    public async Task PreparesExecutesRepeatedlyAndClosesStatement()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        var statement = await connection.PrepareAsync("SELECT ? * 2 AS doubled");

        var first = await statement.QueryAsync(SqlParameters.Create(21));
        var second = await statement.QueryAsync(SqlParameters.Create(100));

        Assert.AreEqual(42, first[0].Get<int>("doubled"));
        Assert.AreEqual(200, second[0].Get<int>("doubled"));

        await statement.DisposeAsync();

        // The statement is closed; the connection itself must remain usable afterward.
        var stillWorks = await connection.QueryAsync("SELECT 1");
        Assert.AreEqual(1, stillWorks[0].Get<int>(0));
    }

    [TestMethod]
    public async Task ReportsAffectedRowsLastInsertIdAndStatus()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE affected_rows_probe (" +
          "id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY, value INT)");
        await using var insert =
          await connection.PrepareAsync("INSERT INTO affected_rows_probe (value) VALUES (?)");

        var first = await insert.ExecuteAsync(SqlParameters.Create(10));
        var second = await insert.ExecuteAsync(SqlParameters.Create(20));

        Assert.AreEqual(1L, first.AffectedRows);
        Assert.AreEqual(1L, second.AffectedRows);
        Assert.IsTrue(first.LastInsertId is > 0);
        Assert.AreEqual(first.LastInsertId!.Value + 1, second.LastInsertId!.Value);
        Assert.AreEqual(1, connection.LastCommandInfo.AffectedRows);
        Assert.AreEqual(second.LastInsertId!.Value, connection.LastCommandInfo.LastInsertId);
        Assert.IsTrue((connection.ServerStatus & MySqlServerStatus.AutoCommit) != 0);

        var update = await connection.ExecuteAsync(
          "UPDATE affected_rows_probe SET value = 99 WHERE value = 10");
        Assert.AreEqual(1L, update.AffectedRows);
    }

    [TestMethod]
    public async Task CommitsAndRollsBackTransactions()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE transaction_probe (value INT)");

        await using (var rolledBack = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
              "INSERT INTO transaction_probe VALUES (?)",
              SqlParameters.Create(1));
            Assert.IsTrue(connection.InTransaction);
        }

        var afterRollback = await connection.QueryAsync("SELECT COUNT(*) FROM transaction_probe");
        Assert.AreEqual(0L, afterRollback[0].Get<long>(0));
        Assert.IsFalse(connection.InTransaction);

        await using (var committed = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
              "INSERT INTO transaction_probe VALUES (?)",
              SqlParameters.Create(2));
            await committed.CommitAsync();
        }

        var afterCommit = await connection.QueryAsync("SELECT COUNT(*) FROM transaction_probe");
        Assert.AreEqual(1L, afterCommit[0].Get<long>(0));
    }

    [TestMethod]
    public async Task ExecutesPreparedBatchInSubmissionOrder()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE batch_probe (value INT)");
        await using var statement =
          await connection.PrepareAsync("INSERT INTO batch_probe VALUES (?)");
        var batch = Enumerable.Range(0, 20)
          .Select(static value => SqlParameters.Create(value))
          .ToArray();

        var results = await statement.ExecuteBatchAsync(batch);

        Assert.AreEqual(20, results.Count);
        Assert.IsTrue(results.All(static result => result.AffectedRows == 1));
        var rows = await connection.QueryAsync("SELECT value FROM batch_probe ORDER BY value");
        CollectionAssert.AreEqual(
          Enumerable.Range(0, 20).ToArray(),
          rows.Select(static row => row.Get<int>("value")).ToArray());
    }

    [TestMethod]
    public async Task PreparedBindingFailureLeavesStatementReusable()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await using var statement = await connection.PrepareAsync("SELECT ? AS value");

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
          () => statement.QueryAsync(
            SqlParameters.Create(SqlValue.From(new object()))).AsTask());

        Assert.AreEqual(
          42,
          (await statement.QueryAsync(SqlParameters.Create(42)))[0].GetInt32("value"));
    }

    [TestMethod]
    public async Task BoundsPreparedCacheUnderConcurrencyAndEviction()
    {
        await using var connection = await MySqlClient.ConnectAsync(
          Options with
          {
              CachePreparedStatements = true,
              PreparedStatementCacheSize = 2,
              PreparedStatementCacheSqlLengthLimit = 128,
              PipeliningLimit = 16,
          });
        var pending = Enumerable.Range(0, 32)
          .Select(value => connection.QueryAsync(
            "SELECT CAST(? AS SIGNED) AS value",
            SqlParameters.Create(value)).AsTask())
          .ToArray();
        var results = await Task.WhenAll(pending);
        for (var index = 0; index < results.Length; index++)
        {
            Assert.AreEqual(index, results[index][0].GetInt32(0));
        }

        _ = await connection.QueryAsync("SELECT CAST(? AS SIGNED) + 1", SqlParameters.Create(1));
        _ = await connection.QueryAsync("SELECT CAST(? AS SIGNED) + 2", SqlParameters.Create(1));
        Assert.AreEqual(
          42,
          (await connection.QueryAsync(
            "SELECT CAST(? AS SIGNED) AS value",
            SqlParameters.Create(42)))[0].GetInt32(0));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task BypassesPreparedCacheAboveSqlLengthLimit()
    {
        await using var connection = await MySqlClient.ConnectAsync(
          Options with
          {
              CachePreparedStatements = true,
              PreparedStatementCacheSize = 8,
              PreparedStatementCacheSqlLengthLimit = 1,
          });
        var before = await ReadPreparedStatementCountAsync(connection);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync(
            "SELECT CAST(? AS SIGNED) AS value",
            SqlParameters.Create(42)))[0].GetInt32(0));
        var after = await ReadPreparedStatementCountAsync(connection);

        Assert.AreEqual(before, after);

        static async ValueTask<long> ReadPreparedStatementCountAsync(
            MySqlConnection connection)
        {
            var rows = await connection.QueryAsync(
              "SHOW GLOBAL STATUS LIKE 'Prepared_stmt_count'");
            return long.Parse(
              rows[0].GetString("Value"),
              System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [TestMethod]
    public async Task PreparedBatchReportsFailedIndexAndSuccessfulPrefix()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE batch_failure_probe (value INT PRIMARY KEY)");
        await using var statement =
          await connection.PrepareAsync("INSERT INTO batch_failure_probe VALUES (?)");
        SqlParameters[] batch =
        [
            SqlParameters.Create(1),
            SqlParameters.Create(2),
            SqlParameters.Create(1),
            SqlParameters.Create(3),
        ];

        var exception = await Assert.ThrowsExactlyAsync<MySqlBatchException>(
          () => statement.ExecuteBatchAsync(batch).AsTask());

        Assert.AreEqual(2, exception.FailedIndex);
        Assert.HasCount(2, exception.SuccessfulResults);
        Assert.IsTrue(exception.SuccessfulResults.All(static result => result.AffectedRows == 1));
        Assert.AreEqual(42, (await connection.QueryAsync("SELECT 42"))[0].Get<int>(0));
    }

    [TestMethod]
    public async Task PingsAndResetsSessionState()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.PingAsync();
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE reset_probe (value INT)");
        await connection.ExecuteAsync("INSERT INTO reset_probe VALUES (1)");

        await connection.ResetAsync();

        await connection.PingAsync();
        await Assert.ThrowsExactlyAsync<MySqlException>(
          () => connection.QueryAsync("SELECT * FROM reset_probe").AsTask());
        Assert.AreEqual(42, (await connection.QueryAsync("SELECT 42"))[0].Get<int>(0));
    }

    [TestMethod]
    public async Task UseAffectedRowsDistinguishesChangedFromMatchedRows()
    {
        await using var matched = await MySqlClient.ConnectAsync(
          Options with { UseAffectedRows = false });
        await matched.ExecuteAsync("CREATE TEMPORARY TABLE affected_probe (value INT)");
        await matched.ExecuteAsync("INSERT INTO affected_probe VALUES (1)");
        var matchedResult = await matched.ExecuteAsync(
          "UPDATE affected_probe SET value = 1 WHERE value = 1");

        await using var changed = await MySqlClient.ConnectAsync(
          Options with { UseAffectedRows = true });
        await changed.ExecuteAsync("CREATE TEMPORARY TABLE affected_probe (value INT)");
        await changed.ExecuteAsync("INSERT INTO affected_probe VALUES (1)");
        var changedResult = await changed.ExecuteAsync(
          "UPDATE affected_probe SET value = 1 WHERE value = 1");

        Assert.AreEqual(1L, matchedResult.AffectedRows);
        Assert.AreEqual(0L, changedResult.AffectedRows);
    }

    [TestMethod]
    public async Task DecodesAndEncodesNegativeExtendedTime()
    {
        var expected = -new TimeSpan(34, 22, 59, 59, 123, 456);
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE time_probe (value TIME(6))");
        await using (var insert = await connection.PrepareAsync(
                       "INSERT INTO time_probe VALUES (?)"))
        {
            await insert.ExecuteAsync(SqlParameters.Create(SqlValue.From(expected)));
        }

        var text = (await connection.QueryAsync("SELECT value FROM time_probe"))[0];
        await using var select = await connection.PrepareAsync("SELECT value FROM time_probe");
        var binary = (await select.QueryAsync())[0];

        Assert.AreEqual(expected, text.Get<TimeSpan>(0));
        Assert.AreEqual(expected, binary.Get<TimeSpan>(0));
    }

    [TestMethod]
    public async Task AppliesFractionalTemporalColumnPrecision()
    {
        var inputTime = new TimeSpan(0, 11, 12, 0, 123, 456);
        var expectedTime = new TimeSpan(0, 11, 12, 0, 123, 500);
        var inputDateTime = new DateTime(
          2026, 1, 2, 3, 4, 5, 123, 456, DateTimeKind.Unspecified);
        var expectedDateTime = new DateTime(
          2026, 1, 2, 3, 4, 5, 123, 500, DateTimeKind.Unspecified);
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE temporal_precision_probe " +
          "(time_value TIME(4), datetime_value DATETIME(4))");
        await using (var insert = await connection.PrepareAsync(
                       "INSERT INTO temporal_precision_probe VALUES (?, ?)"))
        {
            await insert.ExecuteAsync(SqlParameters.Create(
              SqlValue.From(inputTime),
              inputDateTime));
        }

        var text = (await connection.QueryAsync(
          "SELECT time_value, datetime_value FROM temporal_precision_probe"))[0];
        await using var select = await connection.PrepareAsync(
          "SELECT time_value, datetime_value FROM temporal_precision_probe");
        var binary = (await select.QueryAsync())[0];

        Assert.AreEqual(expectedTime, text.Get<TimeSpan>("time_value"));
        Assert.AreEqual(expectedTime, binary.Get<TimeSpan>("time_value"));
        Assert.AreEqual(expectedDateTime, text.Get<DateTime>("datetime_value"));
        Assert.AreEqual(expectedDateTime, binary.Get<DateTime>("datetime_value"));
    }

    [TestMethod]
    public async Task AppliesAllZeroDateBehaviors()
    {
        const string sql = "SELECT value FROM zero_date_probe";
        await using (var errors = await MySqlClient.ConnectAsync(
                       Options with { ZeroDateBehavior = MySqlZeroDateBehavior.Error }))
        {
            await SeedZeroDateAsync(errors);
            var row = (await errors.QueryAsync(sql))[0];
            Assert.ThrowsExactly<FormatException>(() => row.Get<DateOnly>(0));
        }

        await using (var nulls = await MySqlClient.ConnectAsync(
                       Options with { ZeroDateBehavior = MySqlZeroDateBehavior.Null }))
        {
          await SeedZeroDateAsync(nulls);
            var row = (await nulls.QueryAsync(sql))[0];
            Assert.IsTrue(row.IsNull(0));
            Assert.IsNull(row.Get<DateOnly?>(0));
        }

        await using (var minimum = await MySqlClient.ConnectAsync(
                       Options with { ZeroDateBehavior = MySqlZeroDateBehavior.MinValue }))
        {
          await SeedZeroDateAsync(minimum);
            Assert.AreEqual(
              DateOnly.MinValue,
              (await minimum.QueryAsync(sql))[0].Get<DateOnly>(0));
        }

        static async Task SeedZeroDateAsync(MySqlConnection connection)
        {
          await connection.ExecuteAsync("SET SESSION sql_mode = 'ALLOW_INVALID_DATES'");
          await connection.ExecuteAsync("CREATE TEMPORARY TABLE zero_date_probe (value DATE)");
          await connection.ExecuteAsync("INSERT INTO zero_date_probe VALUES ('0000-00-00')");
        }
    }

    [TestMethod]
    public async Task ReadsMultipleResultSetsFromAStoredProcedure()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        var procedureName = "multi_result_" + Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(
          $"CREATE PROCEDURE {procedureName}() " +
          "BEGIN SELECT 1 AS a; SELECT 'two' AS b; END");
        try
        {
            var first = await connection.QueryAsync($"CALL {procedureName}()");

            Assert.AreEqual(1, first[0].Get<int>("a"));
            Assert.IsNotNull(first.Next);
            Assert.AreEqual("two", first.Next![0].Get<string>("b"));
        }
        finally
        {
            await connection.ExecuteAsync($"DROP PROCEDURE IF EXISTS {procedureName}");
        }
    }

    [TestMethod]
    public async Task DecodesSignedAndUnsignedIntegerBoundariesThroughTextAndBinaryProtocols()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE integer_matrix (" +
          "c_tinyint TINYINT, c_tinyint_u TINYINT UNSIGNED, " +
          "c_smallint SMALLINT, c_smallint_u SMALLINT UNSIGNED, " +
          "c_int INT, c_int_u INT UNSIGNED, " +
          "c_bigint BIGINT, c_bigint_u BIGINT UNSIGNED)");
        await using (var insert = await connection.PrepareAsync(
          "INSERT INTO integer_matrix VALUES (?, ?, ?, ?, ?, ?, ?, ?)"))
        {
            await insert.ExecuteAsync(SqlParameters.Create(
              SqlValue.From((sbyte)sbyte.MinValue),
              SqlValue.From((byte)byte.MaxValue),
              (short)short.MinValue,
              SqlValue.From((ushort)ushort.MaxValue),
              int.MinValue,
              SqlValue.From((uint)uint.MaxValue),
              long.MinValue,
              SqlValue.From(ulong.MaxValue)));
        }

        await AssertIntegerMatrixAsync(await connection.QueryAsync("SELECT * FROM integer_matrix"));
        await using var select =
          await connection.PrepareAsync("SELECT * FROM integer_matrix");
        await AssertIntegerMatrixAsync(await select.QueryAsync());

        static Task AssertIntegerMatrixAsync(SqlRowSet rows)
        {
            var row = rows[0];
            Assert.AreEqual(sbyte.MinValue, row.Get<sbyte>("c_tinyint"));
            Assert.AreEqual(byte.MaxValue, row.Get<byte>("c_tinyint_u"));
            Assert.AreEqual(short.MinValue, row.Get<short>("c_smallint"));
            Assert.AreEqual(ushort.MaxValue, row.Get<ushort>("c_smallint_u"));
            Assert.AreEqual(int.MinValue, row.Get<int>("c_int"));
            Assert.AreEqual(uint.MaxValue, row.Get<uint>("c_int_u"));
            Assert.AreEqual(long.MinValue, row.Get<long>("c_bigint"));
            Assert.AreEqual(ulong.MaxValue, row.Get<ulong>("c_bigint_u"));
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task DecodesDecimalFloatingPointStringBinaryAndBitColumns()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE scalar_matrix (" +
          "c_decimal DECIMAL(20,4), c_float FLOAT, c_double DOUBLE, " +
          "c_varchar VARCHAR(64), c_varbinary VARBINARY(64), c_blob BLOB, c_bit BIT(16))");
        await using (var insert = await connection.PrepareAsync(
          "INSERT INTO scalar_matrix VALUES (?, ?, ?, ?, ?, ?, ?)"))
        {
            await insert.ExecuteAsync(SqlParameters.Create(
              12345.6789m,
              1.5f,
              2.25d,
              "héllo",
              new byte[] { 1, 2, 3 },
              new byte[] { 9, 8, 7, 6 },
              (short)0x0102));
        }

        await AssertScalarMatrixAsync(await connection.QueryAsync("SELECT * FROM scalar_matrix"));
        await using var select =
          await connection.PrepareAsync("SELECT * FROM scalar_matrix");
        await AssertScalarMatrixAsync(await select.QueryAsync());

        static Task AssertScalarMatrixAsync(SqlRowSet rows)
        {
            var row = rows[0];
            Assert.AreEqual(12345.6789m, row.Get<decimal>("c_decimal"));
            Assert.AreEqual(1.5f, row.Get<float>("c_float"));
            Assert.AreEqual(2.25d, row.Get<double>("c_double"));
            Assert.AreEqual("héllo", row.Get<string>("c_varchar"));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, row.Get<byte[]>("c_varbinary"));
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, row.Get<byte[]>("c_blob"));
            Assert.AreEqual(0x0102ul, row.Get<ulong>("c_bit"));
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task RoundTripsArbitraryPrecisionDecimal()
    {
        const string text =
          "12345678901234567890123456789012345.123456789012345678901234567890";
        MySqlDecimal value = MySqlDecimal.Parse(text);
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE large_decimal_probe (value DECIMAL(65,30))");
        await connection.ExecuteAsync(
          "INSERT INTO large_decimal_probe VALUES (?)",
          SqlParameters.Create(value));

        var textRows = await connection.QueryAsync(
          "SELECT value FROM large_decimal_probe");
        await using var select = await connection.PrepareAsync(
          "SELECT value FROM large_decimal_probe");
        var binaryRows = await select.QueryAsync();

        Assert.AreEqual(value, textRows[0].Get<MySqlDecimal>(0));
        Assert.AreEqual(value, binaryRows[0].Get<MySqlDecimal>(0));
        Assert.AreEqual(text, textRows[0].Get<MySqlDecimal>(0).ToString());
    }

    [TestMethod]
    public async Task RoundTripsBclScalarAlternatives()
    {
        BigInteger integer = BigInteger.Parse(
          "123456789012345678901234567890",
          CultureInfo.InvariantCulture);
        IPAddress address = IPAddress.Parse("192.0.2.1");
        PhysicalAddress physicalAddress = PhysicalAddress.Parse("08-00-2B-01-02-03");
        BitArray bits = new(new[] { true, false, true, true });
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE bcl_scalar_matrix (" +
          "c_integer DECIMAL(65,0), c_character CHAR(1), c_characters VARCHAR(20), " +
          "c_address VARCHAR(45), c_physical VARBINARY(8), c_bits BIT(4), " +
          "c_half FLOAT, c_int128 DECIMAL(65,0), c_uint128 DECIMAL(65,0))");
        await using (var insert = await connection.PrepareAsync(
          "INSERT INTO bcl_scalar_matrix VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)"))
        {
            await insert.ExecuteAsync(SqlParameters.Create(
              SqlValue.From(integer),
              SqlValue.From('x'),
              SqlValue.From("hello".ToCharArray()),
              SqlValue.From(address),
              SqlValue.From(physicalAddress),
              SqlValue.From(bits),
              SqlValue.From((Half)1.5f),
              SqlValue.From(Int128.MinValue),
              SqlValue.From(UInt128.MaxValue)));
        }

        await AssertValuesAsync(await connection.QueryAsync("SELECT * FROM bcl_scalar_matrix"));
        await using var select = await connection.PrepareAsync("SELECT * FROM bcl_scalar_matrix");
        await AssertValuesAsync(await select.QueryAsync());

        Task AssertValuesAsync(SqlRowSet rows)
        {
            var row = rows[0];
            Assert.AreEqual(integer, row.Get<BigInteger>("c_integer"));
            Assert.AreEqual('x', row.Get<char>("c_character"));
            CollectionAssert.AreEqual("hello".ToCharArray(), row.Get<char[]>("c_characters"));
            Assert.AreEqual(address, row.Get<IPAddress>("c_address"));
            Assert.AreEqual(physicalAddress, row.Get<PhysicalAddress>("c_physical"));
            BitArray decodedBits = row.Get<BitArray>("c_bits");
            CollectionAssert.AreEqual(
              new[] { true, false, true, true },
              Enumerable.Range(0, decodedBits.Count).Select(index => decodedBits[index]).ToArray());
            Assert.AreEqual((Half)1.5f, row.Get<Half>("c_half"));
            Assert.AreEqual(Int128.MinValue, row.Get<Int128>("c_int128"));
            Assert.AreEqual(UInt128.MaxValue, row.Get<UInt128>("c_uint128"));
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task DecodesTemporalYearAndJsonColumns()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE temporal_matrix (" +
          "c_year YEAR, c_date DATE, c_time TIME, c_datetime DATETIME(6), " +
          "c_timestamp TIMESTAMP(6) NULL, c_json JSON)");
        DateOnly date = new(2024, 3, 15);
        TimeSpan time = new(13, 45, 30);
        var dateTime = new DateTime(2024, 3, 15, 13, 45, 30, DateTimeKind.Unspecified).AddTicks(1234560);
        await using (var insert = await connection.PrepareAsync(
          "INSERT INTO temporal_matrix VALUES (?, ?, ?, ?, ?, ?)"))
        {
            await insert.ExecuteAsync(SqlParameters.Create(
              2024,
              date,
              SqlValue.From(time),
              dateTime,
              dateTime,
              """{"ok":true}"""));
        }

        await AssertTemporalMatrixAsync(await connection.QueryAsync("SELECT * FROM temporal_matrix"));
        await using var select =
          await connection.PrepareAsync("SELECT * FROM temporal_matrix");
        await AssertTemporalMatrixAsync(await select.QueryAsync());

        Task AssertTemporalMatrixAsync(SqlRowSet rows)
        {
            var row = rows[0];
            Assert.AreEqual(2024, row.Get<int>("c_year"));
            Assert.AreEqual(date, row.Get<DateOnly>("c_date"));
            Assert.AreEqual(time, row.Get<TimeSpan>("c_time"));
            Assert.AreEqual(dateTime, row.Get<DateTime>("c_datetime"));
            Assert.AreEqual(dateTime, row.Get<DateTime>("c_timestamp"));
            // MySQL canonicalizes stored JSON text (adds a space after ':'), so parse it instead of
            // comparing the raw string.
            using JsonDocument json = JsonDocument.Parse(row.Get<string>("c_json"));
            Assert.IsTrue(json.RootElement.GetProperty("ok").GetBoolean());
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task DecodesEnumAndSetColumnsAsStrings()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync(
          "CREATE TEMPORARY TABLE enum_matrix (" +
          "c_enum ENUM('small','medium','large'), c_set SET('a','b','c'))");
        await connection.ExecuteAsync(
          "INSERT INTO enum_matrix VALUES ('medium', 'a,c')");

        var textRows = await connection.QueryAsync("SELECT * FROM enum_matrix");
        Assert.AreEqual("medium", textRows[0].Get<string>("c_enum"));
        Assert.AreEqual("a,c", textRows[0].Get<string>("c_set"));

        await using var select =
          await connection.PrepareAsync("SELECT * FROM enum_matrix");
        var binaryRows = await select.QueryAsync();
        Assert.AreEqual("medium", binaryRows[0].Get<string>("c_enum"));
        Assert.AreEqual("a,c", binaryRows[0].Get<string>("c_set"));
    }

    [TestMethod]
    public async Task AppliesConnectionTableAndColumnCollationsWithEmoji()
    {
        const string value = "😀 café 漢字";
        await using var connection = await MySqlClient.ConnectAsync(Options);
        var session = (await connection.QueryAsync(
          "SELECT @@character_set_connection, @@collation_connection"))[0];
        await connection.ExecuteAsync(
          """
          CREATE TEMPORARY TABLE collation_probe (
            inherited_value VARCHAR(64),
            binary_value VARCHAR(64) COLLATE utf8mb4_bin
          ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
          """);
        await connection.ExecuteAsync(
          "INSERT INTO collation_probe VALUES (?, ?)",
          SqlParameters.Create(value, value));
        var row = (await connection.QueryAsync(
          """
          SELECT
            inherited_value,
            binary_value,
            CAST(inherited_value = '😀 CAFÉ 漢字' AS SIGNED) AS inherited_matches,
            CAST(binary_value = '😀 CAFÉ 漢字' AS SIGNED) AS binary_matches
          FROM collation_probe
          """))[0];

        Assert.AreEqual("utf8mb4", session.GetString(0));
        StringAssert.StartsWith(session.GetString(1), "utf8mb4_");
        Assert.AreEqual(value, row.GetString("inherited_value"));
        Assert.AreEqual(value, row.GetString("binary_value"));
        Assert.AreEqual(1L, row.GetInt64("inherited_matches"));
        Assert.AreEqual(0L, row.GetInt64("binary_matches"));
    }

    [TestMethod]
    public async Task DecodesGeometryColumnAsRawBytes()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE geometry_matrix (c_point POINT)");
        await connection.ExecuteAsync(
          "INSERT INTO geometry_matrix VALUES (ST_PointFromText('POINT(1 1)'))");

        var rows = await connection.QueryAsync(
          "SELECT ST_AsBinary(c_point) AS wkb FROM geometry_matrix");

        var wellKnownBinary = rows[0].Get<byte[]>("wkb");
        Assert.IsGreaterThan(0, wellKnownBinary.Length);
    }

    [TestMethod]
    public async Task NullValuesRoundTripAsNullInBothProtocols()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE nullable_probe (value INT)");
        await connection.ExecuteAsync(
          "INSERT INTO nullable_probe VALUES (?)",
          SqlParameters.Create(SqlValue.Null));

        var textRows = await connection.QueryAsync("SELECT value FROM nullable_probe");
        Assert.IsTrue(textRows[0].IsNull(0));
        Assert.IsNull(textRows[0].Get<int?>(0));

        await using var select =
          await connection.PrepareAsync("SELECT value FROM nullable_probe");
        var binaryRows = await select.QueryAsync();
        Assert.IsTrue(binaryRows[0].IsNull(0));
    }

    [TestMethod]
    public async Task RowsRemainValidAfterConnectionDisposal()
    {
        var connection = await MySqlClient.ConnectAsync(Options);
        var rows = await connection.QueryAsync("SELECT 1 AS id, 'safe' AS message");
        await connection.DisposeAsync();

        Assert.AreEqual(1, rows[0].Get<int>("id"));
        Assert.AreEqual("safe", rows[0].Get<string>("message"));
    }

    [TestMethod]
    public async Task StreamsAndReusesBorrowedReaderRepeatedly()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);

        for (var iteration = 0; iteration < 5; iteration++)
        {
            List<int> streamed = [];
            await foreach (var row in connection.StreamAsync(
                             "SELECT 1 AS v UNION ALL SELECT 2 UNION ALL SELECT 3",
                             fetchSize: 2))
            {
                streamed.Add(row.Get<int>("v"));
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, streamed);

            await using var reader = await connection.ExecuteReaderAsync(
              "SELECT 1 AS v UNION ALL SELECT 2 UNION ALL SELECT 3");
            List<int> borrowed = [];
            while (await reader.ReadAsync())
            {
                borrowed.Add(reader.GetInt32(0));
            }

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, borrowed);
        }
    }

    [TestMethod]
    public async Task DisposingAStreamEarlyStillLeavesTheConnectionReusable()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);

        await foreach (var row in connection.StreamAsync(
                         "SELECT 1 AS v UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4",
                         fetchSize: 1))
        {
            Assert.AreEqual(1, row.Get<int>("v"));
            break;
        }

        var rows = await connection.QueryAsync("SELECT 42 AS answer");
        Assert.AreEqual(42, rows[0].Get<int>("answer"));
    }

    [TestMethod]
    public async Task PoolLeasePinsTheUnderlyingConnectionUntilTheReaderIsDisposed()
    {
        await using MySqlPool pool = MySqlPool.Create(
          Options,
          new SqlPoolOptions { MaximumSize = 1, MaximumWaitQueueSize = 0 });

        var leased = await pool.GetConnectionAsync();
        await using (var reader =
                       await leased.ExecuteReaderAsync("SELECT 1 AS v UNION ALL SELECT 2"))
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));

            // The pool has no free slot and no wait queue, so a second lease must fail immediately
            // instead of ever handing out a second physical connection while this one stays open.
            await Assert.ThrowsExactlyAsync<SqlClientException>(
              () => pool.GetConnectionAsync().AsTask());
        }

        await leased.DisposeAsync();

        var released = await pool.GetConnectionAsync();
        await released.DisposeAsync();
    }

    [TestMethod]
    public async Task PoolServesConcurrentQueriesUnderLoad()
    {
        await using MySqlPool pool = MySqlPool.Create(Options, new SqlPoolOptions { MaximumSize = 8 });

        var queries = Enumerable.Range(0, 64)
          .Select(index => pool.QueryAsync($"SELECT {index} AS v").AsTask())
          .ToArray();
        var results = await Task.WhenAll(queries);

        for (var i = 0; i < results.Length; i++)
        {
            Assert.AreEqual(i, results[i][0].Get<int>("v"));
        }

        Assert.IsLessThanOrEqualTo(8, pool.Size);
    }

    [TestMethod]
    public async Task CancellationInterruptsAQueryAndTheConnectionStaysReusable()
    {
        await using var connection = await MySqlClient.ConnectAsync(
          Options with { QueryCancellation = MySqlQueryCancellation.KillQuery });
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
          () => connection.QueryAsync("SELECT SLEEP(10)", cancellation.Token).AsTask());

        var rows = await connection.QueryAsync("SELECT 1 AS v");
        Assert.AreEqual(1, rows[0].Get<int>("v"));
    }

    [TestMethod]
    public async Task CancellationDoesNotExposeAnUndeliveredBorrowedRow()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);
        using CancellationTokenSource cancellation = new();
        await using (var reader =
                     await connection.ExecuteReaderAsync("SELECT 42 AS v", cancellationToken: cancellation.Token))
        {
            await Task.Delay(100);
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
              () => reader.ReadAsync().AsTask());
        }

        var rows = await connection.QueryAsync("SELECT 43 AS v");
        Assert.AreEqual(43, rows[0].GetInt32("v"));
    }

    [TestMethod]
    public async Task SurfacesMySqlErrorNumberAndSqlState()
    {
        await using var connection = await MySqlClient.ConnectAsync(Options);

        var exception = await Assert.ThrowsExactlyAsync<MySqlException>(
          () => connection.QueryAsync("SELECT * FROM no_such_table_xyz").AsTask());

        Assert.AreEqual(1146, exception.ErrorNumber);
        Assert.IsFalse(string.IsNullOrEmpty(exception.SqlState));
    }

      private static int ReserveUnusedPort()
      {
        System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
          return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
          listener.Stop();
        }
      }

      private static string EscapeLocalInfilePath(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal);
}
