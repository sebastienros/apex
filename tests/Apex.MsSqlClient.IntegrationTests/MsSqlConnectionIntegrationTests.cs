using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using Apex.SqlClient;

namespace Apex.MsSqlClient.IntegrationTests;

[TestClass]
public sealed class MsSqlConnectionIntegrationTests
{
    [TestMethod]
    public async Task ConnectsQueriesMetadataAndExecutesParameterizedRpc()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);

        var scalar = await connection.QueryAsync(
          "SELECT 1 AS id, CAST(N'hello' AS nvarchar(20)) AS message, DB_NAME() AS database_name");

        Assert.IsTrue(connection.IsSecure);
        Assert.AreEqual("Microsoft SQL Server", connection.DatabaseMetadata.ProductName);
        Assert.IsGreaterThanOrEqualTo(15, connection.DatabaseMetadata.MajorVersion);
        Assert.AreEqual(1, scalar.Count);
        Assert.AreEqual(1, scalar[0].GetInt32("id"));
        Assert.AreEqual("hello", scalar[0].GetString("message"));
        Assert.AreEqual("master", scalar[0].GetString("database_name"));

        var parameterized = await connection.QueryAsync(
          "SELECT @P1 AS id, @P2 AS message",
          SqlParameters.Create(42, "forty-two"));
        Assert.AreEqual(42, parameterized[0].Get<int>("id"));
        Assert.AreEqual("forty-two", parameterized[0].GetString("message"));
    }

      [TestMethod]
      [DataRow(MsSqlEncryptionMode.Optional)]
      [DataRow(MsSqlEncryptionMode.Require)]
      public async Task SupportsEncryptedConnectionModes(MsSqlEncryptionMode mode)
      {
        await using var connection = await MsSqlClient.ConnectAsync(
          MsSqlTestEnvironment.Options with
          {
            EncryptionMode = mode,
            TrustServerCertificate = true,
          });

        Assert.IsTrue(connection.IsSecure);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].GetInt32(0));
      }

      [TestMethod]
      public async Task StrictEncryptionRejectsServerWithoutTds8()
      {
        await Assert.ThrowsExactlyAsync<IOException>(
          () => MsSqlClient.ConnectAsync(
          MsSqlTestEnvironment.Options with
          {
            EncryptionMode = MsSqlEncryptionMode.Strict,
            TrustServerCertificate = true,
          }).AsTask());
      }

      [TestMethod]
      public async Task RejectsUntrustedServerCertificate()
      {
        await Assert.ThrowsExactlyAsync<System.Security.Authentication.AuthenticationException>(
          () => MsSqlClient.ConnectAsync(
          MsSqlTestEnvironment.Options with
          {
            EncryptionMode = MsSqlEncryptionMode.Require,
            TrustServerCertificate = false,
            CertificateValidationCallback = null,
          }).AsTask());
      }

      [TestMethod]
      public async Task CertificateCallbackControlsTrustAndUsesTlsHostName()
      {
        System.Net.Security.SslPolicyErrors observedErrors = default;
        await using var connection = await MsSqlClient.ConnectAsync(
          MsSqlTestEnvironment.Options with
          {
            EncryptionMode = MsSqlEncryptionMode.Require,
            TrustServerCertificate = false,
            TlsHostName = "apex-mssql.invalid",
            CertificateValidationCallback = (_, certificate, _, errors) =>
            {
              Assert.IsNotNull(certificate);
              observedErrors = errors;
              return true;
            },
          });

        Assert.IsTrue(connection.IsSecure);
        Assert.AreNotEqual(System.Net.Security.SslPolicyErrors.None, observedErrors);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].GetInt32(0));
      }

    [TestMethod]
    public async Task RollsBackTransactionOnDispose()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await connection.ExecuteAsync("CREATE TABLE #rollback_values (value int NOT NULL)");

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
              "INSERT INTO #rollback_values VALUES (@P1)",
              SqlParameters.Create(1));
        }

        var count = await connection.QueryAsync(
          "SELECT COUNT(*) AS value_count FROM #rollback_values");
        Assert.AreEqual(0, count[0].GetInt32("value_count"));
    }

    [TestMethod]
    public async Task SurfacesStructuredErrorsAndInfoMessages()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        List<MsSqlInfo> messages = [];
        connection.InfoMessage += messages.Add;

        await connection.ExecuteAsync("RAISERROR(N'apex-info', 10, 1)");

        Assert.HasCount(1, messages);
        Assert.AreEqual(50000, messages[0].Number);
        Assert.AreEqual(0, messages[0].Severity);
        StringAssert.Contains(messages[0].Message, "apex-info");

        var exception = await Assert.ThrowsExactlyAsync<MsSqlException>(
          () => connection.QueryAsync("SELECT missing_column").AsTask());
        Assert.AreEqual(207, exception.Number);
        Assert.AreEqual(16, exception.Severity);
        Assert.AreEqual(1, exception.State);
        Assert.HasCount(1, exception.Errors);
        StringAssert.Contains(exception.Message, "missing_column");
        Assert.IsGreaterThan(0, exception.LineNumber);
    }

    [TestMethod]
    public async Task RejectsInvalidDatabaseUsernameAndPassword()
    {
        var options = MsSqlTestEnvironment.Options;
        var database = await Assert.ThrowsExactlyAsync<MsSqlException>(
          () => MsSqlClient.ConnectAsync(
            options with { Database = "missing_database" }).AsTask());
        var username = await Assert.ThrowsExactlyAsync<MsSqlException>(
          () => MsSqlClient.ConnectAsync(
            options with { Username = "missing_user" }).AsTask());
        var password = await Assert.ThrowsExactlyAsync<MsSqlException>(
          () => MsSqlClient.ConnectAsync(
            options with { Password = "wrong_password" }).AsTask());

        Assert.AreEqual(4060, database.Number);
        Assert.AreEqual(18456, username.Number);
        Assert.AreEqual(18456, password.Number);
    }

      [TestMethod]
      public async Task ExhaustsConfiguredReconnectAttempts()
      {
        var port = ReserveUnusedPort();
        var options = MsSqlTestEnvironment.Options with
        {
          Host = "127.0.0.1",
          Port = port,
          ConnectTimeout = TimeSpan.FromMilliseconds(100),
          ReconnectAttempts = 2,
          ReconnectInterval = TimeSpan.FromMilliseconds(100),
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(
          () => MsSqlClient.ConnectAsync(options).AsTask());

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180), stopwatch.Elapsed);
      }

    [TestMethod]
    public async Task ExecutesPreparedStatementAndBatch()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await connection.ExecuteAsync("CREATE TABLE #batch_values (value int NOT NULL)");
        await using var statement =
          await connection.PrepareAsync("INSERT INTO #batch_values VALUES (@P1)");

        var first = await statement.ExecuteAsync(SqlParameters.Create(-1));
        var batch = Enumerable.Range(0, 16)
          .Select(static value => SqlParameters.Create(value))
          .ToArray();
        var results = await statement.ExecuteBatchAsync(batch);

        Assert.AreEqual(1L, first.AffectedRows);
        Assert.HasCount(16, results);
        Assert.IsTrue(results.All(static result => result.AffectedRows == 1));
        var rows = await connection.QueryAsync(
          "SELECT value FROM #batch_values ORDER BY value");
        CollectionAssert.AreEqual(
          Enumerable.Range(-1, 17).ToArray(),
          rows.Select(static row => row.GetInt32(0)).ToArray());

        await using var query =
          await connection.PrepareAsync("SELECT @P1 AS value");
        await using (var reader =
                     await query.ExecuteReaderAsync(SqlParameters.Create(100)))
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(100, reader.GetInt32("value"));
            Assert.IsFalse(await reader.ReadAsync());
        }

        Assert.AreEqual(
          101,
          (await query.QueryAsync(SqlParameters.Create(101)))[0].GetInt32(0));
        List<int> streamed = [];
        await foreach (var row in query.StreamAsync(
                         SqlParameters.Create(102),
                         fetchSize: 1))
        {
            streamed.Add(row.GetInt32(0));
        }

        CollectionAssert.AreEqual(new[] { 102 }, streamed);

        await using var streamFirst =
          await connection.PrepareAsync("SELECT @P1 AS value");
        List<int> firstStream = [];
        await foreach (var row in streamFirst.StreamAsync(
                         SqlParameters.Create(200),
                         fetchSize: 1))
        {
            firstStream.Add(row.GetInt32(0));
        }

        CollectionAssert.AreEqual(new[] { 200 }, firstStream);
        Assert.AreEqual(
          201,
          (await streamFirst.QueryAsync(SqlParameters.Create(201)))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task PreparedBatchFailureKeepsConnectionSynchronized()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await connection.ExecuteAsync(
          "CREATE TABLE #batch_failure_values (value int PRIMARY KEY)");
        await using var statement = await connection.PrepareAsync(
          "INSERT INTO #batch_failure_values VALUES (@P1)");
        SqlParameters[] batch =
        [
            SqlParameters.Create(1),
            SqlParameters.Create(2),
            SqlParameters.Create(1),
            SqlParameters.Create(3),
        ];

        var exception = await Assert.ThrowsExactlyAsync<MsSqlException>(
          () => statement.ExecuteBatchAsync(batch).AsTask());

        Assert.IsTrue(exception.Number is 2601 or 2627);
        var rows = await connection.QueryAsync(
          "SELECT value FROM #batch_failure_values ORDER BY value");
        CollectionAssert.AreEqual(
          new[] { 1, 2, 3 },
          rows.Select(static row => row.GetInt32(0)).ToArray());
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT CAST(42 AS int)"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task ReturnsOutputRowsAndStoredProcedureResults()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await connection.ExecuteAsync("CREATE TABLE #output_values (value int NOT NULL)");

        var output = await connection.QueryAsync(
          "INSERT INTO #output_values OUTPUT inserted.value VALUES (@P1)",
          SqlParameters.Create(42));

        Assert.HasCount(1, output);
        Assert.AreEqual(1L, output.AffectedRows);
        Assert.AreEqual(42, output[0].GetInt32(0));

        var procedure = "apex_result_" + Guid.NewGuid().ToString("N");
        await connection.ExecuteAsync(
          $"CREATE PROCEDURE {procedure} @value int AS SELECT @value AS value");
        try
        {
            var rows = await connection.QueryAsync(
              $"EXEC {procedure} @value = @P1",
              SqlParameters.Create(43));
            Assert.HasCount(1, rows);
            Assert.AreEqual(43, rows[0].GetInt32("value"));
        }
        finally
        {
            await connection.ExecuteAsync($"DROP PROCEDURE {procedure}");
        }
    }

    [TestMethod]
    public async Task DecodesAndEncodesTypeMatrixIncludingPlpValues()
    {
        Guid guid = Guid.Parse("12345678-1234-5678-9012-123456789abc");
        DateOnly date = new(2026, 8, 14);
        TimeOnly time = new(12, 34, 56, 123, 456);
        DateTime dateTime = new(
          2026,
          8,
          14,
          12,
          34,
          56,
          123,
          456,
          DateTimeKind.Unspecified);
        DateTimeOffset dateTimeOffset = new(
          2026,
          8,
          14,
          12,
          34,
          56,
          123,
          456,
          TimeSpan.FromHours(2.5));

        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var decodedRows = await connection.QueryAsync(
          """
      SELECT
        CAST(1 AS bit) AS boolean_value,
        CAST(2 AS tinyint) AS byte_value,
        CAST(-3 AS smallint) AS int16_value,
        CAST(4 AS int) AS int32_value,
        CAST(5 AS bigint) AS int64_value,
        CAST(1.5 AS real) AS single_value,
        CAST(2.5 AS float) AS double_value,
        CAST(123456789012345.6789 AS decimal(19,4)) AS decimal_value,
        CAST('12345678-1234-5678-9012-123456789abc' AS uniqueidentifier) AS guid_value,
        CAST('2026-08-14' AS date) AS date_value,
        CAST('12:34:56.1234560' AS time(7)) AS time_value,
        CAST('2026-08-14T12:34:56.1234560' AS datetime2(7)) AS datetime2_value,
        CAST('2026-08-14T12:34:56.1234560+02:30' AS datetimeoffset(7)) AS datetimeoffset_value,
        CAST(N'apex-text' AS nvarchar(20)) AS text_value,
        CAST(0x0001FEFF AS varbinary(4)) AS binary_value,
        CAST(NULL AS int) AS null_value
      """);
        var decoded = decodedRows[0];

        Assert.IsTrue(decoded.Get<bool>("boolean_value"));
        Assert.AreEqual((byte)2, decoded.Get<byte>("byte_value"));
        Assert.AreEqual((short)-3, decoded.GetInt16("int16_value"));
        Assert.AreEqual(4, decoded.GetInt32("int32_value"));
        Assert.AreEqual(5L, decoded.GetInt64("int64_value"));
        Assert.AreEqual(1.5f, decoded.GetFloat("single_value"));
        Assert.AreEqual(2.5d, decoded.GetDouble("double_value"));
        Assert.AreEqual(123456789012345.6789m, decoded.Get<decimal>("decimal_value"));
        Assert.AreEqual(guid, decoded.GetGuid("guid_value"));
        Assert.AreEqual(date, decoded.GetDateOnly("date_value"));
        Assert.AreEqual(time, decoded.GetTimeOnly("time_value"));
        Assert.AreEqual(dateTime, decoded.GetDateTime("datetime2_value"));
        Assert.AreEqual(dateTimeOffset, decoded.GetDateTimeOffset("datetimeoffset_value"));
        Assert.AreEqual("apex-text", decoded.GetString("text_value"));
        CollectionAssert.AreEqual(
          new byte[] { 0, 1, 254, 255 },
          decoded.GetBytes("binary_value"));
        Assert.IsTrue(decoded.IsNull(decoded.GetOrdinal("null_value")));

        string longText = new('x', 9001);
        var longBinary = Enumerable.Range(0, 9001)
          .Select(static value => (byte)(value % 251))
          .ToArray();
        var encoded = (await connection.QueryAsync(
          """
      SELECT
        @P1 AS boolean_value,
        @P2 AS int16_value,
        @P3 AS int32_value,
        @P4 AS int64_value,
        @P5 AS single_value,
        @P6 AS double_value,
        @P7 AS decimal_value,
        @P8 AS guid_value,
        @P9 AS date_value,
        @P10 AS time_value,
        @P11 AS datetime2_value,
        @P12 AS datetimeoffset_value,
        @P13 AS text_value,
        @P14 AS binary_value,
        CAST(@P15 AS int) AS null_value,
        CAST(
          SQL_VARIANT_PROPERTY(CAST(@P3 AS sql_variant), 'BaseType')
          AS nvarchar(128)
        ) AS int_base_type
      """,
          SqlParameters.Create(
            true,
            (short)-2,
            3,
            4L,
            1.25f,
            2.5d,
            123456789012345.6789m,
            guid,
            date,
            time,
            dateTime,
            dateTimeOffset,
            longText,
            longBinary,
            SqlValue.Null)))[0];

        Assert.IsTrue(encoded.Get<bool>("boolean_value"));
        Assert.AreEqual((short)-2, encoded.GetInt16("int16_value"));
        Assert.AreEqual(3, encoded.GetInt32("int32_value"));
        Assert.AreEqual(4L, encoded.GetInt64("int64_value"));
        Assert.AreEqual(1.25f, encoded.GetFloat("single_value"));
        Assert.AreEqual(2.5d, encoded.GetDouble("double_value"));
        Assert.AreEqual(123456789012345.6789m, encoded.Get<decimal>("decimal_value"));
        Assert.AreEqual(guid, encoded.GetGuid("guid_value"));
        Assert.AreEqual(date, encoded.GetDateOnly("date_value"));
        Assert.AreEqual(time, encoded.GetTimeOnly("time_value"));
        Assert.AreEqual(dateTime, encoded.GetDateTime("datetime2_value"));
        Assert.AreEqual(dateTimeOffset, encoded.GetDateTimeOffset("datetimeoffset_value"));
        Assert.AreEqual(longText, encoded.GetString("text_value"));
        CollectionAssert.AreEqual(longBinary, encoded.GetBytes("binary_value"));
        Assert.IsTrue(encoded.IsNull(encoded.GetOrdinal("null_value")));
        Assert.AreEqual("int", encoded.GetString("int_base_type"));
    }

    [TestMethod]
    public async Task RoundTripsBclScalarAlternatives()
    {
        BigInteger integer = BigInteger.Parse(
          "123456789012345678901234567890",
          CultureInfo.InvariantCulture);
        TimeSpan duration = TimeSpan.FromHours(12.5);
        IPAddress address = IPAddress.Parse("192.0.2.1");
        PhysicalAddress physicalAddress = PhysicalAddress.Parse("08-00-2B-01-02-03");
        BitArray bits = new(new[] { true, false, true, true });
        Int128 int128 = Int128.Parse(
          "-99999999999999999999999999999999999999",
          CultureInfo.InvariantCulture);
        UInt128 uint128 = UInt128.Parse(
          "99999999999999999999999999999999999999",
          CultureInfo.InvariantCulture);

        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var row = (await connection.QueryAsync(
          """
          SELECT
            CAST(@P1 AS numeric(38,0)) AS integer_value,
            CAST(@P2 AS time(7)) AS duration_value,
            CAST(@P3 AS smallint) AS sbyte_value,
            CAST(@P4 AS nchar(1)) AS character_value,
            CAST(@P5 AS nvarchar(20)) AS characters_value,
            CAST(@P6 AS nvarchar(45)) AS address_value,
            CAST(@P7 AS varbinary(8)) AS physical_address_value,
            CAST(@P8 AS varchar(64)) AS bits_value,
            CAST(@P9 AS real) AS half_value,
            CAST(@P10 AS numeric(38,0)) AS int128_value,
            CAST(@P11 AS numeric(38,0)) AS uint128_value
          """,
          SqlParameters.Create(
            SqlValue.From(integer),
            SqlValue.From(duration),
            SqlValue.From((sbyte)-128),
            SqlValue.From('x'),
            SqlValue.From("hello".ToCharArray()),
            SqlValue.From(address),
            SqlValue.From(physicalAddress),
            SqlValue.From(bits),
            SqlValue.From((Half)1.5f),
            SqlValue.From(int128),
            SqlValue.From(uint128))))[0];

        Assert.AreEqual(integer, row.Get<BigInteger>("integer_value"));
        Assert.AreEqual(duration, row.Get<TimeSpan>("duration_value"));
        Assert.AreEqual((sbyte)-128, row.Get<sbyte>("sbyte_value"));
        Assert.AreEqual('x', row.Get<char>("character_value"));
        CollectionAssert.AreEqual("hello".ToCharArray(), row.Get<char[]>("characters_value"));
        Assert.AreEqual(address, row.Get<IPAddress>("address_value"));
        Assert.AreEqual(physicalAddress, row.Get<PhysicalAddress>("physical_address_value"));
        BitArray decodedBits = row.Get<BitArray>("bits_value");
        CollectionAssert.AreEqual(
          new[] { true, false, true, true },
          Enumerable.Range(0, decodedBits.Count).Select(index => decodedBits[index]).ToArray());
        Assert.AreEqual((Half)1.5f, row.Get<Half>("half_value"));
        Assert.AreEqual(int128, row.Get<Int128>("int128_value"));
        Assert.AreEqual(uint128, row.Get<UInt128>("uint128_value"));
    }

    [TestMethod]
    public async Task BufferedRowsRemainValidAfterConnectionDisposal()
    {
        SqlRow row;
        await using (var connection =
                     await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options))
        {
            row = (await connection.QueryAsync(
              "SELECT 42 AS value, CAST(N'safe' AS nvarchar(20)) AS label"))[0];
        }

        Assert.AreEqual(42, row.GetInt32("value"));
        Assert.AreEqual("safe", row.GetString("label"));
    }

    [TestMethod]
    public async Task ReadsBorrowedRowsWithTypedGettersAndRepeatedStress()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await using (var reader = await connection.ExecuteReaderAsync(
                       """
                   SELECT
                     CAST(42 AS int) AS value,
                     CAST(N'borrowed' AS nvarchar(20)) AS label,
                     CAST('2026-08-14' AS date) AS date_value,
                     CAST(0x0102FEFF AS varbinary(4)) AS bytes_value
                   """))
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(4, reader.FieldCount);
            Assert.AreEqual(0, reader.GetOrdinal("value"));
            Assert.AreEqual(42, reader.GetInt32(0));
            Assert.AreEqual("borrowed", reader.GetString(1));
            Assert.AreEqual(new DateOnly(2026, 8, 14), reader.GetDateOnly(2));
            CollectionAssert.AreEqual(
              new byte[] { 1, 2, 254, 255 },
              reader.GetBytes(3));
            Assert.IsFalse(await reader.ReadAsync());
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        for (var iteration = 0; iteration < 250; iteration++)
        {
            await using var reader = await connection.ExecuteReaderAsync(
              """
        WITH sequence(value) AS
        (
          SELECT CAST(1 AS int)
          UNION ALL
          SELECT value + 1 FROM sequence WHERE value < 25
        )
        SELECT value FROM sequence OPTION (MAXRECURSION 25)
        """,
              cancellationToken: timeout.Token);
            var sum = 0;
            while (await reader.ReadAsync(timeout.Token))
            {
                sum += reader.GetInt32(0);
            }

            Assert.AreEqual(325, sum);
        }
    }

    [TestMethod]
    public async Task DecodesDefaultAndUtf8VarcharCollations()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);

        var row = (await connection.QueryAsync(
          """
      SELECT
        CAST('€“smart”' AS varchar(40)) AS default_code_page,
        CAST(
          N'€漢字' COLLATE Latin1_General_100_CI_AS_SC_UTF8
          AS varchar(40)
        ) AS utf8_code_page
      """))[0];

        Assert.AreEqual("€“smart”", row.GetString("default_code_page"));
        Assert.AreEqual("€漢字", row.GetString("utf8_code_page"));
    }

    [TestMethod]
    public async Task RoundsLegacyDateTimeToMilliseconds()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);

        var value = (await connection.QueryAsync(
          "SELECT CAST('2026-01-02T03:04:05.997' AS datetime) AS value"))[0]
          .GetDateTime(0);

        Assert.AreEqual(new DateTime(2026, 1, 2, 3, 4, 5, 997), value);
        Assert.AreEqual(0, value.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [TestMethod]
    public async Task DecodesLegacyLobMoneyAndNullableTypes()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);

        var row = (await connection.QueryAsync(
          """
          SELECT
            CAST(123456789012345.6789 AS money) AS money_value,
            CAST(-1234.5678 AS smallmoney) AS smallmoney_value,
            CAST('2026-01-02T03:04:00' AS smalldatetime) AS smalldatetime_value,
            CAST('fixed' AS char(8)) AS char_value,
            CONVERT(text, 'legacy text') AS text_value,
            CONVERT(ntext, N'legacy unicode 22') AS ntext_value,
            CONVERT(image, 0x0001FEFF) AS image_value,
            CAST(7922816251426433759354395.0335 AS decimal(29,4)) AS decimal_value,
            CAST(NULL AS money) AS null_money,
            CAST(NULL AS smalldatetime) AS null_datetime,
            CAST(NULL AS text) AS null_text,
            CAST(NULL AS image) AS null_image
          """))[0];

        Assert.AreEqual(123456789012345.6789m, row.Get<decimal>("money_value"));
        Assert.AreEqual(-1234.5678m, row.Get<decimal>("smallmoney_value"));
        Assert.AreEqual(
          new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Unspecified),
          row.GetDateTime("smalldatetime_value"));
        Assert.AreEqual("fixed   ", row.GetString("char_value"));
        Assert.AreEqual("legacy text", row.GetString("text_value"));
        Assert.AreEqual("legacy unicode 22", row.GetString("ntext_value"));
        CollectionAssert.AreEqual(
          new byte[] { 0, 1, 254, 255 },
          row.GetBytes("image_value"));
        Assert.AreEqual(
          7922816251426433759354395.0335m,
          row.Get<decimal>("decimal_value"));
        Assert.IsNull(row.Get<decimal?>("null_money"));
        Assert.IsNull(row.Get<DateTime?>("null_datetime"));
        Assert.IsNull(row.Get<string?>("null_text"));
        Assert.IsNull(row.Get<byte[]?>("null_image"));
    }

    [TestMethod]
    public async Task DecodesNullableScalarMatrix()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var row = (await connection.QueryAsync(
          """
          SELECT
            CAST(NULL AS bit) AS boolean_value,
            CAST(NULL AS tinyint) AS byte_value,
            CAST(NULL AS smallint) AS int16_value,
            CAST(NULL AS int) AS int32_value,
            CAST(NULL AS bigint) AS int64_value,
            CAST(NULL AS real) AS single_value,
            CAST(NULL AS float) AS double_value,
            CAST(NULL AS decimal(19,4)) AS decimal_value,
            CAST(NULL AS uniqueidentifier) AS guid_value,
            CAST(NULL AS date) AS date_value,
            CAST(NULL AS time(7)) AS time_value,
            CAST(NULL AS datetime2(7)) AS datetime_value,
            CAST(NULL AS datetimeoffset(7)) AS datetimeoffset_value,
            CAST(NULL AS nvarchar(20)) AS string_value,
            CAST(NULL AS varbinary(20)) AS bytes_value
          """))[0];

        Assert.IsNull(row.Get<bool?>("boolean_value"));
        Assert.IsNull(row.Get<byte?>("byte_value"));
        Assert.IsNull(row.Get<short?>("int16_value"));
        Assert.IsNull(row.Get<int?>("int32_value"));
        Assert.IsNull(row.Get<long?>("int64_value"));
        Assert.IsNull(row.Get<float?>("single_value"));
        Assert.IsNull(row.Get<double?>("double_value"));
        Assert.IsNull(row.Get<decimal?>("decimal_value"));
        Assert.IsNull(row.Get<Guid?>("guid_value"));
        Assert.IsNull(row.Get<DateOnly?>("date_value"));
        Assert.IsNull(row.Get<TimeOnly?>("time_value"));
        Assert.IsNull(row.Get<DateTime?>("datetime_value"));
        Assert.IsNull(row.Get<DateTimeOffset?>("datetimeoffset_value"));
        Assert.IsNull(row.Get<string?>("string_value"));
        Assert.IsNull(row.Get<byte[]?>("bytes_value"));
    }

    [TestMethod]
    public async Task EncodesNullParametersAcrossScalarTypeFamilies()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var parameters = SqlParameters.Create(
          Enumerable.Repeat(SqlValue.Null, 15).ToArray());
        var row = (await connection.QueryAsync(
          """
          SELECT
            CAST(@P1 AS bit), CAST(@P2 AS tinyint), CAST(@P3 AS smallint),
            CAST(@P4 AS int), CAST(@P5 AS bigint), CAST(@P6 AS real),
            CAST(@P7 AS float), CAST(@P8 AS decimal(19,4)),
            CAST(@P9 AS uniqueidentifier), CAST(@P10 AS date),
            CAST(@P11 AS time(7)), CAST(@P12 AS datetime2(7)),
            CAST(@P13 AS datetimeoffset(7)), CAST(@P14 AS nvarchar(20)),
            CAST(@P15 AS varbinary(20))
          """,
          parameters))[0];

        Assert.AreEqual(15, row.Count);
        for (var ordinal = 0; ordinal < row.Count; ordinal++)
        {
            Assert.IsTrue(row.IsNull(ordinal), $"Column {ordinal} should be NULL.");
        }
    }

    [TestMethod]
    public async Task EncodesParametersAtFixedAndMaxLengthBoundaries()
    {
        var fixedText = new string('x', 4000);
        var maxText = new string('x', 4001);
        var fixedBytes = new byte[8000];
        var maxBytes = new byte[8001];
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);

        var row = (await connection.QueryAsync(
          """
          SELECT
            CAST(DATALENGTH(@P1) AS bigint) AS fixed_text_length,
            CAST(DATALENGTH(@P2) AS bigint) AS max_text_length,
            CAST(DATALENGTH(@P3) AS bigint) AS fixed_binary_length,
            CAST(DATALENGTH(@P4) AS bigint) AS max_binary_length
          """,
          SqlParameters.Create(fixedText, maxText, fixedBytes, maxBytes)))[0];

        Assert.AreEqual(8000L, row.GetInt64("fixed_text_length"));
        Assert.AreEqual(8002L, row.GetInt64("max_text_length"));
        Assert.AreEqual(8000L, row.GetInt64("fixed_binary_length"));
        Assert.AreEqual(8001L, row.GetInt64("max_binary_length"));
    }

    [TestMethod]
    public async Task DecodesXmlAsTextAndPreservesEmptyResultMetadata()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        const string xml = "<root><value>apex</value></root>";

        var row = (await connection.QueryAsync(
          $"SELECT CAST(N'{xml}' AS xml) AS payload"))[0];
        await using var reader = await connection.ExecuteReaderAsync(
          "SELECT CAST(NULL AS xml) AS payload WHERE 1 = 0");

        Assert.AreEqual(xml, row.GetString("payload"));
        Assert.IsFalse(await reader.ReadAsync());
        Assert.AreEqual(1, reader.FieldCount);
        Assert.AreEqual("payload", reader.Columns[0].Name);
    }

    [TestMethod]
    public async Task StreamsMultipleResultSetsWithTheirOwnMetadata()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        List<SqlRow> rows = [];

        await foreach (var row in connection.StreamAsync(
          "SELECT CAST(1 AS int) AS a; SELECT CAST(N'x' AS nvarchar(10)) AS b",
          fetchSize: 50))
        {
            rows.Add(row);
        }

        Assert.HasCount(2, rows);
        Assert.AreEqual(0, rows[0].GetOrdinal("a"));
        Assert.AreEqual(1, rows[0].GetInt32(0));
        Assert.AreEqual(0, rows[1].GetOrdinal("b"));
        Assert.AreEqual("x", rows[1].GetString(0));
    }

    [TestMethod]
    public async Task DecodesSqlServer2025NativeJson()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        if (connection.DatabaseMetadata.MajorVersion < 17)
        {
            Assert.Inconclusive("The native json type requires SQL Server 2025 or later.");
        }

        const string json = """{"name":"apex","values":[1,2]}""";
        var row = (await connection.QueryAsync(
          $$"""SELECT CAST(N'{{json}}' AS json) AS payload"""))[0];

        Assert.AreEqual(json, row.GetString("payload"));
    }

    [TestMethod]
    public async Task StopsClientStreamEarlyAndReusesConnection()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        List<int> values = [];
        await foreach (var row in connection.StreamAsync(
                         """
                     WITH sequence(value) AS
                     (
                       SELECT CAST(1 AS int)
                       UNION ALL
                       SELECT value + 1 FROM sequence WHERE value < 10000
                     )
                     SELECT value FROM sequence OPTION (MAXRECURSION 0)
                     """,
                         fetchSize: 2))
        {
            values.Add(row.GetInt32(0));
            if (values.Count == 3)
            {
                break;
            }
        }

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT CAST(42 AS int)"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task CancelsWaitForWithAttentionAndReusesConnection()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250));

        var exception =
          await Assert.ThrowsAsync<OperationCanceledException>(
            () => connection.QueryAsync(
              "WAITFOR DELAY '00:00:10'; SELECT CAST(1 AS int)",
              cancellation.Token).AsTask());

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT CAST(42 AS int)"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task ServerCloseFailsInFlightCommandAndConnection()
    {
        await using var victim =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await using var killer =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var sessionId = (await victim.QueryAsync(
          "SELECT CAST(@@SPID AS int)"))[0].GetInt32(0);
        var pending = victim.QueryAsync(
          "WAITFOR DELAY '00:00:10'; SELECT CAST(1 AS int)").AsTask();
        await Task.Delay(200);

        await killer.ExecuteAsync($"KILL {sessionId}");
        await Assert.ThrowsAsync<Exception>(() => pending);
        await Assert.ThrowsAsync<Exception>(
          () => victim.QueryAsync("SELECT CAST(1 AS int)").AsTask());
    }

    [TestMethod]
    public async Task PoolPinsLeaseUntilBorrowedReaderIsDisposed()
    {
        await using MsSqlPool pool = MsSqlPool.Create(
          MsSqlTestEnvironment.Options,
          new SqlPoolOptions
          {
              MaximumSize = 1,
              AcquisitionTimeout = TimeSpan.FromSeconds(5),
          });
        var first = await pool.GetConnectionAsync();
        var reader = await first.ExecuteReaderAsync(
          """
      SELECT CAST(1 AS int) AS value
      UNION ALL
      SELECT CAST(2 AS int)
      """);
        await first.DisposeAsync();

        var pending = pool.GetConnectionAsync().AsTask();
        await Task.Delay(100);
        Assert.IsFalse(pending.IsCompleted);

        await reader.DisposeAsync();
        await using var second = await pending;
        Assert.AreEqual(
          1,
          (await second.QueryAsync("SELECT CAST(1 AS int)"))[0].GetInt32(0));
        Assert.AreEqual(1, pool.Size);
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
}
