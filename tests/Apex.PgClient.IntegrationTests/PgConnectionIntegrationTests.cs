using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Transactions;
using Apex.SqlClient;
using Apex.SqlClient.SpecificationTests;
using Testcontainers.PostgreSql;

namespace Apex.PgClient.IntegrationTests;

[TestClass]
public sealed class PgConnectionIntegrationTests
{
    private PostgreSqlContainer? _container;

    [TestInitialize]
    public async Task StartPostgreSqlAsync()
    {
        var image = Environment.GetEnvironmentVariable("POSTGRES_IMAGE") ?? "postgres:16-alpine";
        _container = new PostgreSqlBuilder(image)
            .WithDatabase("db")
            .WithUsername("user")
            .WithPassword("pass")
            .Build();
        await _container.StartAsync();
    }

    [TestCleanup]
    public async Task StopPostgreSqlAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ConnectsQueriesAndRollsBack()
    {
        var container = _container ??
            throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        var scalar = await connection.QueryAsync("SELECT 1 AS id, 'hello' AS message");

        Assert.AreEqual(1, scalar.Count);
        Assert.AreEqual(1, scalar[0].Get<int>(0));
        Assert.AreEqual("hello", scalar[0].Get<string>("message"));
        Assert.AreEqual("PostgreSQL", connection.DatabaseMetadata.ProductName);
        Assert.IsGreaterThanOrEqualTo(14, connection.DatabaseMetadata.MajorVersion);

        var parameterized = await connection.QueryAsync(
            "SELECT $1::int4 AS id, $2::text AS message",
            SqlParameters.Create(42, "forty-two"));
        Assert.AreEqual(42, parameterized[0].Get<int>("id"));
        Assert.AreEqual("forty-two", parameterized[0].Get<string>("message"));

        await connection.ExecuteAsync("CREATE TEMP TABLE values_to_rollback (value int)");
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync("INSERT INTO values_to_rollback VALUES (1)");
        }

        var count = await connection.QueryAsync(
            "SELECT COUNT(*)::int8 AS count FROM values_to_rollback");
        Assert.AreEqual(0L, count[0].Get<long>("count"));
    }

    [TestMethod]
    public async Task SupportsTypedParametersBatchesTransactionsCopyAndTypeReload()
    {
        var container = _container ??
            throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
            using JsonDocument json = JsonDocument.Parse("""{"name":"Apex"}""");
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var timestamps = new[]
            {
                DateTimeOffset.Parse("2026-08-16T12:00:00Z", CultureInfo.InvariantCulture),
            };
            var typed = await connection.QueryTypedAsync(
                """
                SELECT $1::jsonb ->> 'name' AS name,
                       $2::uuid[] AS ids,
                       $3::timestamptz[] AS timestamps,
                       $4::jsonb IS NULL AS is_null
                """,
                PgParameters.Create(
                    PgParameter.Create(PgType.Jsonb, json),
                    PgParameter.Create(PgType.UuidArray, ids),
                    PgParameter.Create(PgType.TimestampTzArray, timestamps),
                    new PgParameter(PgType.Jsonb, SqlValue.Null)));
            Assert.AreEqual("Apex", typed[0].Get<string>("name"));
            CollectionAssert.AreEqual(ids, typed[0].GetArray<Guid>("ids"));
            Assert.IsTrue(typed[0].Get<bool>("is_null"));

            PgBatch batch = new();
            batch.Add(
                "SELECT $1::int4 AS value",
                PgParameters.Create(PgParameter.Create(PgType.Integer, 42)));
            batch.Add(
                "SELECT $1::jsonb ->> 'name' AS name",
                PgParameters.Create(PgParameter.Create(PgType.Jsonb, json)));
            var batchResults = await connection.ExecuteBatchAsync(batch);
            Assert.AreEqual(2, batchResults.Count);
            Assert.AreEqual(42, batchResults.Current[0].Get<int>("value"));
            Assert.IsTrue(await batchResults.NextResultAsync());
            Assert.AreEqual("Apex", batchResults.Current[0].Get<string>("name"));

            PgBatch failingBatch = new();
            failingBatch.Add("SELECT 1");
            failingBatch.Add("SELECT missing_column");
            var batchException = await Assert.ThrowsExactlyAsync<PgBatchException>(
                () => connection.ExecuteBatchAsync(failingBatch).AsTask());
            Assert.AreEqual(1, batchException.CommandIndex);
            Assert.AreEqual(PgErrorCodes.UndefinedColumn, batchException.ServerError.SqlState);
            Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));

            await connection.ExecuteAsync("CREATE TEMP TABLE transaction_values (value int)");
            await using (var transaction = await connection.BeginPgTransactionAsync(
                new PgTransactionOptions
                {
                    IsolationLevel = PgIsolationLevel.Serializable,
                }))
            {
                await connection.ExecuteAsync("INSERT INTO transaction_values VALUES (1)");
                await transaction.CreateSavepointAsync("before_second");
                await connection.ExecuteAsync("INSERT INTO transaction_values VALUES (2)");
                await transaction.RollbackToSavepointAsync("before_second");
                await transaction.ReleaseSavepointAsync("before_second");
                await transaction.CommitAsync();
            }

            Assert.AreEqual(
                1L,
                (await connection.QueryAsync(
                    "SELECT count(*)::int8 AS count FROM transaction_values"))[0].Get<long>("count"));

            await connection.ExecuteAsync(
                """
                CREATE TEMP TABLE copied_values (
                    id int4,
                    data jsonb,
                    external_id uuid,
                    created_at timestamptz)
                """);
            await using (var importer = await connection.BeginBinaryImportAsync(
                "COPY copied_values (id, data, external_id, created_at) FROM STDIN (FORMAT BINARY)"))
            {
                await importer.StartRowAsync();
                await importer.WriteAsync(PgParameter.Create(PgType.Integer, 1));
                await importer.WriteAsync(PgParameter.Create(PgType.Jsonb, json));
                await importer.WriteAsync(PgParameter.Create(PgType.Uuid, ids[0]));
                await importer.WriteAsync(PgParameter.Create(PgType.TimestampTz, timestamps[0]));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => connection.QueryAsync("SELECT 1").AsTask());
                await importer.CompleteAsync();
            }

            Assert.AreEqual(
                "Apex",
                (await connection.QueryAsync(
                    "SELECT data ->> 'name' AS name FROM copied_values"))[0].Get<string>("name"));
            await using (var importer = await connection.BeginBinaryImportAsync(
                "COPY copied_values (id, data, external_id, created_at) FROM STDIN (FORMAT BINARY)"))
            {
                await importer.StartRowAsync();
            }

            Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));

            await connection.ExecuteAsync(
                "CREATE TYPE apex_test_mood AS ENUM ('happy', 'sad')");
            await connection.ReloadTypesAsync();
            Assert.IsTrue(
                connection.TypeRegistry.TryGetType(
                    "public.apex_test_mood",
                    out var moodType));
            connection.TypeRegistry.Register<string>(
                moodType,
                Encoding.UTF8.GetBytes,
                value => Encoding.UTF8.GetString(value.Span));
            var mood = await connection.QueryTypedAsync(
                "SELECT $1::apex_test_mood AS mood",
                PgParameters.Create(PgParameter.Create(moodType, "happy")));
            Assert.AreEqual("happy", mood[0].Get<string>("mood"));

            await connection.ExecuteAsync("CREATE TEMP TABLE ambient_values (value int)");
            using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
            {
                await connection.EnlistTransactionAsync(Transaction.Current!);
                await connection.ExecuteAsync("INSERT INTO ambient_values VALUES (1)");
                scope.Complete();
            }
            using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
            {
                await connection.EnlistTransactionAsync(Transaction.Current!);
                await connection.ExecuteAsync("INSERT INTO ambient_values VALUES (2)");
            }

            Assert.AreEqual(
            1L,
            (await connection.QueryAsync(
                "SELECT count(*)::int8 AS count FROM ambient_values"))[0].Get<long>("count"));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => connection.BeginBinaryImportAsync(
                    "COPY copied_values (id, data, external_id, created_at) FROM STDIN (FORMAT TEXT)")
                  .AsTask());
            await Assert.ThrowsAsync<Exception>(
                () => connection.QueryAsync("SELECT 1").AsTask());
    }

    [TestMethod]
    public async Task RoundTripsBclScalarAlternatives()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        BigInteger integer = BigInteger.Parse(
          "123456789012345678901234567890",
          CultureInfo.InvariantCulture);
        TimeSpan duration = TimeSpan.FromHours(26.5);
        IPAddress address = IPAddress.Parse("192.0.2.1");
        PhysicalAddress physicalAddress = PhysicalAddress.Parse("08-00-2B-01-02-03");
        BitArray bits = new(new[] { true, false, true, true });

        await using var connection = await PgClient.ConnectAsync(options);
        var row = (await connection.QueryAsync(
          """
          SELECT
            $1::numeric AS integer_value,
            $2::interval AS duration_value,
            $3::text AS character_value,
            $4::text AS characters_value,
            $5::int2 AS byte_value,
            $6::int2 AS sbyte_value,
            $7::inet AS address_value,
            $8::macaddr AS physical_address_value,
            $9::bit(4) AS bits_value,
            $10::numeric[] AS integer_values,
            $11::interval[] AS duration_values,
            $12::inet[] AS address_values,
            $13::macaddr[] AS physical_address_values,
            $14::bit(3)[] AS bits_values,
            $15::int2[] AS sbyte_values,
            $16::text[] AS character_array_values,
            $17::float4 AS half_value,
            $18::numeric AS int128_value,
            $19::numeric AS uint128_value,
            $20::float4[] AS half_values,
            $21::numeric[] AS int128_values,
            $22::numeric[] AS uint128_values
          """,
          SqlParameters.Create(
            SqlValue.From(integer),
            SqlValue.From(duration),
            SqlValue.From('x'),
            SqlValue.From("hello".ToCharArray()),
            SqlValue.From((byte)255),
            SqlValue.From((sbyte)-128),
            SqlValue.From(address),
            SqlValue.From(physicalAddress),
            SqlValue.From(bits),
            SqlValue.From(new[] { BigInteger.One, new BigInteger(2) }),
            SqlValue.From(new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2) }),
            SqlValue.From(new[] { address, IPAddress.Parse("2001:db8::1") }),
            SqlValue.From(new[] { physicalAddress }),
            SqlValue.From(new[] { new BitArray(new[] { true, false, true }) }),
            SqlValue.From(new sbyte[] { -128, 127 }),
            SqlValue.From(new[] { "a".ToCharArray(), "bc".ToCharArray() }),
            SqlValue.From((Half)1.5f),
            SqlValue.From(Int128.MinValue),
            SqlValue.From(UInt128.MaxValue),
            SqlValue.From(new Half[] { (Half)1.5f, (Half)(-2.25f) }),
            SqlValue.From(new Int128[] { Int128.MinValue, Int128.MaxValue }),
            SqlValue.From(new UInt128[] { UInt128.Zero, UInt128.MaxValue }))))[0];

        Assert.AreEqual(integer, row.Get<BigInteger>("integer_value"));
        Assert.AreEqual(duration, row.Get<TimeSpan>("duration_value"));
        Assert.AreEqual('x', row.Get<char>("character_value"));
        CollectionAssert.AreEqual("hello".ToCharArray(), row.Get<char[]>("characters_value"));
        Assert.AreEqual((byte)255, row.Get<byte>("byte_value"));
        Assert.AreEqual((sbyte)-128, row.Get<sbyte>("sbyte_value"));
        Assert.AreEqual(address, row.Get<IPAddress>("address_value"));
        Assert.AreEqual(physicalAddress, row.Get<PhysicalAddress>("physical_address_value"));
        Assert.IsTrue(row.Get<BitArray>("bits_value")[2]);
        CollectionAssert.AreEqual(
          new[] { BigInteger.One, new BigInteger(2) },
          row.GetArray<BigInteger>("integer_values"));
        CollectionAssert.AreEqual(
          new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2) },
          row.GetArray<TimeSpan>("duration_values"));
        CollectionAssert.AreEqual(
          new[] { address, IPAddress.Parse("2001:db8::1") },
          row.GetArray<IPAddress>("address_values"));
        Assert.AreEqual(
          physicalAddress,
          row.GetArray<PhysicalAddress>("physical_address_values")![0]);
        Assert.IsTrue(row.GetArray<BitArray>("bits_values")![0][2]);
        CollectionAssert.AreEqual(
          new sbyte[] { -128, 127 },
          row.GetArray<sbyte>("sbyte_values"));
        CollectionAssert.AreEqual(
          "bc".ToCharArray(),
          row.GetArray<char[]>("character_array_values")![1]);
        Assert.AreEqual((Half)1.5f, row.Get<Half>("half_value"));
        Assert.AreEqual(Int128.MinValue, row.Get<Int128>("int128_value"));
        Assert.AreEqual(UInt128.MaxValue, row.Get<UInt128>("uint128_value"));
        CollectionAssert.AreEqual(
          new Half[] { (Half)1.5f, (Half)(-2.25f) },
          row.GetArray<Half>("half_values"));
        CollectionAssert.AreEqual(
          new Int128[] { Int128.MinValue, Int128.MaxValue },
          row.GetArray<Int128>("int128_values"));
        CollectionAssert.AreEqual(
          new UInt128[] { UInt128.Zero, UInt128.MaxValue },
          row.GetArray<UInt128>("uint128_values"));
    }

    [TestMethod]
    public async Task SurfacesPostgreSqlErrorFields()
    {
        var container = _container ??
            throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        var exception = await Assert.ThrowsExactlyAsync<PgException>(
            () => connection.QueryAsync("SELECT missing_column").AsTask());

        Assert.AreEqual("42703", exception.SqlState);
        Assert.IsNotNull(exception.Severity);
    }

    [TestMethod]
    public async Task RejectsInvalidDatabaseUsernameAndPassword()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        var database = await Assert.ThrowsExactlyAsync<PgException>(
          () => PgClient.ConnectAsync(options with { Database = "missing_database" }).AsTask());
        var username = await Assert.ThrowsExactlyAsync<PgException>(
          () => PgClient.ConnectAsync(options with { Username = "missing_user" }).AsTask());
        var password = await Assert.ThrowsExactlyAsync<PgException>(
          () => PgClient.ConnectAsync(options with { Password = "wrong_password" }).AsTask());

        Assert.AreEqual("3D000", database.SqlState);
        Assert.AreEqual("28P01", username.SqlState);
        Assert.AreEqual("28P01", password.SqlState);
    }

      [TestMethod]
      public async Task ExhaustsConfiguredReconnectAttempts()
      {
        var port = ReserveUnusedPort();
        PgConnectOptions options = new()
        {
        Host = "127.0.0.1",
        Port = port,
        Database = "db",
        Username = "user",
        Password = "pass",
        ConnectTimeout = TimeSpan.FromMilliseconds(100),
        ReconnectAttempts = 2,
        ReconnectInterval = TimeSpan.FromMilliseconds(100),
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(
          () => PgClient.ConnectAsync(options).AsTask());

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180), stopwatch.Elapsed);
      }

    [TestMethod]
    public async Task ReceivesNoticeFields()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        PgNotice? notice = null;
        connection.Notice += value => notice = value;

        await connection.ExecuteAsync(
          "DO $$ BEGIN RAISE NOTICE 'apex notice' USING DETAIL = 'detail', HINT = 'hint'; END $$");

        Assert.IsNotNull(notice);
        Assert.AreEqual("apex notice", notice.Message);
        Assert.AreEqual("NOTICE", notice.Severity);
        Assert.AreEqual("00000", notice.SqlState);
        Assert.AreEqual("detail", notice.Detail);
        Assert.AreEqual("hint", notice.Hint);
    }

    [TestMethod]
    public async Task DirectCancelRequestLeavesConnectionReusable()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        var pending = connection.QueryAsync("SELECT pg_sleep(10)").AsTask();
        await Task.Delay(200);

        await connection.CancelRequestAsync();
        var exception = await Assert.ThrowsExactlyAsync<PgException>(() => pending);

        Assert.AreEqual("57014", exception.SqlState);
        Assert.AreEqual(42, (await connection.QueryAsync("SELECT 42::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task InsertReturningProvidesRowsAndAffectedCount()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await connection.ExecuteAsync("CREATE TEMP TABLE returning_probe (value int4)");

        var rows = await connection.QueryAsync(
          "INSERT INTO returning_probe VALUES ($1::int4) RETURNING value",
          SqlParameters.Create(42));

        Assert.HasCount(1, rows);
        Assert.AreEqual(1L, rows.AffectedRows);
        Assert.AreEqual(42, rows[0].GetInt32("value"));
    }

    [TestMethod]
    public async Task FetchesServerCursorAndStreamsWithBackpressure()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await using (var transaction = await connection.BeginTransactionAsync())
        await using (var statement =
                     await connection.PrepareAsync("SELECT generate_series(1, 5)::int4 AS value"))
        await using (var cursor = await statement.OpenCursorAsync(fetchSize: 2))
        {
            var first = await cursor.ReadAsync(2);
            var second = await cursor.ReadAsync(2);
            var third = await cursor.ReadAsync(2);

            CollectionAssert.AreEqual(new[] { 1, 2 }, first.Select(static row => row.Get<int>(0)).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 4 }, second.Select(static row => row.Get<int>(0)).ToArray());
            CollectionAssert.AreEqual(new[] { 5 }, third.Select(static row => row.Get<int>(0)).ToArray());
            Assert.IsFalse(cursor.HasMore);
            await transaction.CommitAsync();
        }

        List<int> streamed = [];
        await foreach (var row in connection.StreamAsync(
                         "SELECT generate_series(1, 5)::int4 AS value",
                         fetchSize: 2))
        {
            streamed.Add(row.Get<int>(0));
        }

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, streamed);

        List<int> preparedStreamed = [];
        await using var preparedStream =
          await connection.PrepareAsync("SELECT generate_series(1, 5)::int4 AS value");
        await foreach (var row in preparedStream.StreamAsync(fetchSize: 2))
        {
            preparedStreamed.Add(row.Get<int>(0));
        }

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, preparedStreamed);
    }

    [TestMethod]
    public async Task StopsClientStreamEarlyAndReusesConnection()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        List<int> values = [];
        await foreach (var row in connection.StreamAsync(
                         "SELECT generate_series(1, 1000000)::int4",
                         fetchSize: 2))
        {
            values.Add(row.Get<int>(0));
            if (values.Count == 3)
            {
                break;
            }
        }

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);
        var rows = await connection.QueryAsync("SELECT 42::int4");
        Assert.AreEqual(42, rows[0].Get<int>(0));

        await using var transaction = await connection.BeginTransactionAsync();
        await foreach (var row in connection.StreamAsync(
                         "SELECT generate_series(1, 100000)::int4",
                         fetchSize: 1))
        {
            Assert.AreEqual(1, row.Get<int>(0));
            break;
        }

        rows = await connection.QueryAsync("SELECT 43::int4");
        Assert.AreEqual(43, rows[0].Get<int>(0));
        await transaction.RollbackAsync();
    }

    [TestMethod]
    public async Task StreamingSendFailureCompletesConsumer()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<System.Text.EncoderFallbackException>(
          async () =>
          {
              await foreach (var _ in connection.StreamAsync(
                           "SELECT $1::text",
                           SqlParameters.Create("\uD800"),
                           cancellationToken: timeout.Token))
              {
              }
          });
    }

    [TestMethod]
    public async Task PreparedStreamDeliversFetchedRowsBeforeServerError()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        await using var connection = await PgClient.ConnectAsync(options);
        await using var statement = await connection.PrepareAsync(
          "SELECT CASE WHEN value = 5 THEN 1 / (value - value) ELSE value END " +
          "FROM generate_series(1, 8) AS value");
        List<int> values = [];

        var exception = await Assert.ThrowsExactlyAsync<PgException>(
          async () =>
          {
              await foreach (var row in statement.StreamAsync(fetchSize: 4))
              {
                  values.Add(row.GetInt32(0));
              }
          });

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, values);
        Assert.AreEqual("22012", exception.SqlState);
        Assert.AreEqual(42, (await connection.QueryAsync("SELECT 42::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task ReadsBorrowedRowsWithTypedGetters()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await using var reader = await connection.ExecuteReaderAsync(
          "SELECT generate_series(1, 3)::int4 AS value, 'text'::text AS label");
        var sum = 0;
        while (await reader.ReadAsync())
        {
            Assert.AreEqual(2, reader.FieldCount);
            Assert.AreEqual(0, reader.GetOrdinal("value"));
            sum += reader.GetInt32("value");
            Assert.AreEqual("text", reader.GetString("label"));
        }

        Assert.AreEqual(6, sum);

        await using var statement =
          await connection.PrepareAsync("SELECT $1::int4 AS value");
        await using var prepared =
          await statement.ExecuteReaderAsync(SqlParameters.Create(42));
        Assert.IsTrue(await prepared.ReadAsync());
        Assert.AreEqual(42, prepared.GetInt32(0));
        Assert.IsFalse(await prepared.ReadAsync());
    }

    [TestMethod]
    public async Task CancellationDoesNotExposeAnUndeliveredBorrowedRow()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        using CancellationTokenSource cancellation = new();
        await using (var reader =
                     await connection.ExecuteReaderAsync(
                       "SELECT 42::int4 AS value",
                       cancellationToken: cancellation.Token))
        {
            await Task.Delay(100);
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
              () => reader.ReadAsync().AsTask());
        }

        var rows = await connection.QueryAsync("SELECT 43::int4");
        Assert.AreEqual(43, rows[0].GetInt32(0));
    }

    [TestMethod]
    public async Task SafeRowsRemainValidAfterConnectionDisposal()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        SqlRow row;
        await using (var connection = await PgClient.ConnectAsync(options))
        {
            row = (await connection.QueryAsync(
              "SELECT 42::int4 AS value, 'safe'::text AS label"))[0];
        }

        Assert.AreEqual(42, row.GetInt32("value"));
        Assert.AreEqual("safe", row.GetString("label"));
    }

    [TestMethod]
    public async Task ReusesRepeatedStringsAndBoxedScalars()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            StringCacheCapacity = 16,
            StringCacheMaximumByteLength = 64,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        var rows = await connection.QueryAsync(
          "SELECT 42::int4 AS value, 'repeated'::text AS label " +
          "FROM generate_series(1, 3)");
        var first = rows[0].GetString("label");
        var second = rows[1].GetString("label");
        var third = rows[2].GetString("label");

        Assert.AreNotSame(first, second);
        Assert.AreSame(second, third);
        Assert.AreSame(rows[0].Get<object>("value"), rows[1].Get<object>("value"));
    }

    [TestMethod]
    public async Task PooledReaderPinsConnectionLease()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using PgPool pool = PgPool.Create(
          options,
          new SqlPoolOptions
          {
              MaximumSize = 1,
              AcquisitionTimeout = TimeSpan.FromSeconds(5),
          });
        var first = await pool.GetConnectionAsync();
        var reader = await first.ExecuteReaderAsync(
          "SELECT generate_series(1, 2)::int4");
        await first.DisposeAsync();

        var pending = pool.GetConnectionAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(pending.IsCompleted);

        await reader.DisposeAsync();
        await using var second = await pending;
        Assert.AreEqual(1, (await second.QueryAsync("SELECT 1::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task ReusesPullReaderRepeatedly()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            await using var reader = await connection.ExecuteReaderAsync(
              "SELECT generate_series(1, 100)::int4",
              cancellationToken: timeout.Token);
            var sum = 0;
            while (await reader.ReadAsync(timeout.Token))
            {
                sum += reader.GetInt32(0);
            }

            Assert.AreEqual(5050, sum);
        }
    }

    [TestMethod]
    public async Task CancelsBorrowedReaderAndReusesConnection()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
        await using var reader = await connection.ExecuteReaderAsync(
          "SELECT pg_sleep(10), 1::int4",
          cancellationToken: cancellation.Token);
        var exception =
          await Assert.ThrowsAsync<OperationCanceledException>(
          () => reader.ReadAsync(cancellation.Token).AsTask());
        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT 42::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task ReaderDescribesColumnsForEmptyResult()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await using var reader = await connection.ExecuteReaderAsync(
          "SELECT 1::int4 AS value WHERE false");

        Assert.IsFalse(await reader.ReadAsync());
        Assert.AreEqual(1, reader.FieldCount);
        Assert.AreEqual("value", reader.Columns[0].Name);
    }

    [TestMethod]
    public async Task CancellationLeavesConnectionReusable()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(
          () => connection.QueryAsync("SELECT pg_sleep(10)", cancellation.Token).AsTask());

        var rows = await connection.QueryAsync("SELECT 1::int4 AS value");
        Assert.AreEqual(1, rows[0].Get<int>(0));
    }

    [TestMethod]
    public async Task DecodesTextAndBinaryTypeMatrix()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        const string projection =
          """
      true AS boolean_value,
      2::int2 AS int2_value,
      3::int4 AS int4_value,
      4::int8 AS int8_value,
      1.5::float4 AS float4_value,
      2.5::float8 AS float8_value,
      12345678901234567890.1234::numeric AS numeric_value,
      '12345678-1234-5678-9012-123456789abc'::uuid AS uuid_value,
      '2026-08-14'::date AS date_value,
      '12:34:56.123456'::time AS time_value,
      '12:34:56+02'::timetz AS timetz_value,
      '2026-08-14 12:34:56.123456'::timestamp AS timestamp_value,
      '2026-08-14 12:34:56.123456+00'::timestamptz AS timestamptz_value,
      interval '1 year 2 months 3 days 4 hours 5 minutes 6.123456 seconds' AS interval_value,
      decode('0001feff', 'hex') AS bytea_value,
      '{"ok":true}'::jsonb AS json_value,
      point(1.5, -2.25) AS point_value,
      '192.0.2.1/24'::inet AS inet_value,
      '2001:db8::/64'::cidr AS cidr_value,
      12.34::money AS money_value,
      ARRAY[1, NULL, 3]::int4[] AS array_value
      """;

        await using var connection = await PgClient.ConnectAsync(options);
        var text = (await connection.QueryAsync("SELECT " + projection))[0];
        AssertTypeValues(text);

        var binary = (await connection.QueryAsync(
          "SELECT " + projection + ", $1::int4 AS parameter_value",
          SqlParameters.Create(42)))[0];
        AssertTypeValues(binary);
        Assert.AreEqual(42, binary.Get<int>("parameter_value"));

        await using ISqlRowReader reader = await connection.ExecuteReaderAsync(
          "SELECT ARRAY[1, NULL, 3]::int4[] AS array_value");
        Assert.IsTrue(await reader.ReadAsync());
        CollectionAssert.AreEqual(
          new int?[] { 1, null, 3 },
          reader.GetArray<int?>("array_value"));
    }

    [TestMethod]
    public async Task EncodesNullParametersAcrossSupportedTypeFamilies()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        const string sql =
          """
          SELECT
            $1::bool, $2::bytea, $3::int2, $4::int4, $5::int8,
            $6::float4, $7::float8, $8::numeric, $9::uuid,
            $10::date, $11::time, $12::timetz, $13::timestamp,
            $14::timestamptz, $15::interval, $16::jsonb,
            $17::point, $18::inet, $19::cidr, $20::money, $21::int4[]
          """;
        var parameters = SqlParameters.Create(
          Enumerable.Repeat(SqlValue.Null, 21).ToArray());

        await using var connection = await PgClient.ConnectAsync(options);
        var row = (await connection.QueryAsync(sql, parameters))[0];

        Assert.AreEqual(21, row.Count);
        for (var ordinal = 0; ordinal < row.Count; ordinal++)
        {
            Assert.IsTrue(row.IsNull(ordinal), $"Column {ordinal} should be NULL.");
        }
    }

    [TestMethod]
    public async Task BorrowedReaderReturnsOwnedByteMemory()
    {
        var container = _container ??
          throw new InvalidOperationException(
            "The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection =
          await PgClient.ConnectAsync(options);
        await using var reader =
          await connection.ExecuteReaderAsync(
            """
        SELECT decode('01', 'hex')
        UNION ALL
        SELECT decode('02', 'hex')
        """);
        Assert.IsTrue(await reader.ReadAsync());
        var first =
          reader.Get<ReadOnlyMemory<byte>>(0);
        Assert.IsTrue(await reader.ReadAsync());
        var second =
          reader.Get<ReadOnlyMemory<byte>>(0);

        CollectionAssert.AreEqual(new byte[] { 1 }, first.ToArray());
        CollectionAssert.AreEqual(new byte[] { 2 }, second.ToArray());
    }

    [TestMethod]
    public async Task RepreparesInvalidatedCachedStatement()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            CachePreparedStatements = true,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        const string sql = "SELECT $1::int4 AS value";
        var first = await connection.QueryAsync(sql, SqlParameters.Create(1));
        await connection.ExecuteAsync("DEALLOCATE ALL");
        var second = await connection.QueryAsync(sql, SqlParameters.Create(2));

        Assert.AreEqual(1, first[0].Get<int>(0));
        Assert.AreEqual(2, second[0].Get<int>(0));
    }

    [TestMethod]
    public async Task BoundsPreparedCacheUnderConcurrencyAndEviction()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            CachePreparedStatements = true,
            PreparedStatementCacheSize = 2,
            PreparedStatementCacheSqlLengthLimit = 128,
            PipeliningLimit = 16,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        var pending = Enumerable.Range(0, 32)
          .Select(value => connection.QueryAsync(
            "SELECT $1::int4 AS value",
            SqlParameters.Create(value)).AsTask())
          .ToArray();
        var results = await Task.WhenAll(pending);
        for (var index = 0; index < results.Length; index++)
        {
            Assert.AreEqual(index, results[index][0].GetInt32(0));
        }

        _ = await connection.QueryAsync("SELECT $1::int4 + 1", SqlParameters.Create(1));
        _ = await connection.QueryAsync("SELECT $1::int4 + 2", SqlParameters.Create(1));
        _ = await connection.QueryAsync("SELECT $1::int4 AS value", SqlParameters.Create(42));
        var count = await connection.QueryAsync(
          "SELECT COUNT(*)::int8 FROM pg_prepared_statements");
        Assert.IsLessThanOrEqualTo(2L, count[0].GetInt64(0));
    }

    [TestMethod]
    public async Task BypassesPreparedCacheAboveSqlLengthLimit()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            CachePreparedStatements = true,
            PreparedStatementCacheSize = 8,
            PreparedStatementCacheSqlLengthLimit = 1,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync(
            "SELECT $1::int4 AS value",
            SqlParameters.Create(42)))[0].GetInt32(0));
        var count = await connection.QueryAsync(
          "SELECT COUNT(*)::int8 FROM pg_prepared_statements");

        Assert.AreEqual(0L, count[0].GetInt64(0));
    }

    [TestMethod]
    public async Task PipelinesCommandsAndContinuesAfterSqlError()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            PipeliningLimit = 16,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        var queries = Enumerable.Range(0, 100)
          .Select(index => connection.QueryAsync(
            "SELECT $1::int4 AS value",
            SqlParameters.Create(index)).AsTask())
          .ToArray();
        var results = await Task.WhenAll(queries);

        for (var i = 0; i < results.Length; i++)
        {
            Assert.AreEqual(i, results[i][0].Get<int>(0));
        }

        var before = connection.QueryAsync("SELECT 1::int4").AsTask();
        var failure = connection.QueryAsync("SELECT missing_column").AsTask();
        var after = connection.QueryAsync("SELECT 2::int4").AsTask();
        Assert.AreEqual(1, (await before)[0].Get<int>(0));
        await Assert.ThrowsExactlyAsync<PgException>(() => failure);
        Assert.AreEqual(2, (await after)[0].Get<int>(0));
    }

    [TestMethod]
    public async Task ReceivesNotificationsAndReconnectsSubscriptions()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var subscriber = await PgClient.SubscribeAsync(
          options,
          static _ => TimeSpan.FromMilliseconds(50));
        await subscriber.SubscribeAsync("apex events");
        await using var sender = await PgClient.ConnectAsync(options);
        await sender.ExecuteAsync("""NOTIFY "apex events", 'first'""");
        var first = await NextNotificationAsync(
          subscriber.Notifications,
          TimeSpan.FromSeconds(5));
        Assert.AreEqual("first", first.Payload);

        var firstProcessId = subscriber.ProcessId;
        await sender.QueryAsync(
          "SELECT pg_terminate_backend($1::int4)",
          SqlParameters.Create(firstProcessId));
        using CancellationTokenSource reconnected = new(TimeSpan.FromSeconds(10));
        while (subscriber.ProcessId == firstProcessId)
        {
            await Task.Delay(25, reconnected.Token);
        }

        await sender.ExecuteAsync("""NOTIFY "apex events", 'second'""");
        var second = await NextNotificationAsync(
          subscriber.Notifications,
          TimeSpan.FromSeconds(5));
        Assert.AreEqual("second", second.Payload);
    }

    [TestMethod]
    public async Task SubscribesAndUnsubscribesQuotedChannel()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        const string channel = "apex quoted \" channel";

        await using var subscriber = await PgClient.SubscribeAsync(options);
        await subscriber.SubscribeAsync(channel);
        Assert.IsTrue(subscriber.Channels.Contains(channel));
        await using var sender = await PgClient.ConnectAsync(options);
        await sender.ExecuteAsync("NOTIFY \"apex quoted \"\" channel\", 'payload'");
        var notification = await NextNotificationAsync(
          subscriber.Notifications,
          TimeSpan.FromSeconds(5));
        Assert.AreEqual(channel, notification.Channel);
        Assert.AreEqual("payload", notification.Payload);

        await subscriber.UnsubscribeAsync(channel);

        Assert.IsFalse(subscriber.Channels.Contains(channel));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
          () => subscriber.SubscribeAsync(new string('x', 64)).AsTask());
    }

    [TestMethod]
    public async Task SubscriberStopsAfterReconnectPolicyIsExhausted()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        await using FaultInjectingTcpProxy proxy = new(
          container.Hostname,
          container.GetMappedPublicPort(5432),
          connectionsToDrop: 0);
        PgConnectOptions options = new()
        {
            Host = "127.0.0.1",
            Port = proxy.Port,
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        await using var subscriber = await PgClient.SubscribeAsync(
          options,
          attempt => attempt < 2 ? TimeSpan.FromMilliseconds(25) : null);
        await subscriber.SubscribeAsync("reconnect_exhaustion");
        proxy.RejectNewConnections();
        proxy.CloseActiveConnections();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using var notifications = subscriber.Notifications
          .GetAsyncEnumerator(timeout.Token);

        await Assert.ThrowsAsync<Exception>(
          () => notifications.MoveNextAsync().AsTask());

        Assert.IsGreaterThanOrEqualTo(3, proxy.AcceptedConnections);
    }

    [TestMethod]
    public async Task EnforcesLayer7PreparedStatementScope()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            UseLayer7Proxy = true,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
          () => connection.PrepareAsync("SELECT 1").AsTask());

        await using var transaction = await connection.BeginTransactionAsync();
        await using var statement =
          await connection.PrepareAsync("SELECT 1::int4");
        Assert.AreEqual(1, (await statement.QueryAsync())[0].Get<int>(0));
        await statement.DisposeAsync();
        await transaction.CommitAsync();
    }

    [TestMethod]
    public async Task ExecutesPreparedBatchInSubmissionOrder()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            PipeliningLimit = 8,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await connection.ExecuteAsync("CREATE TEMP TABLE batch_values (value int4)");
        await using var statement =
          await connection.PrepareAsync("INSERT INTO batch_values VALUES ($1::int4)");
        var batch = Enumerable.Range(0, 20)
          .Select(static value => SqlParameters.Create(value))
          .ToArray();
        var results = await statement.ExecuteBatchAsync(batch);

        Assert.AreEqual(20, results.Count);
        Assert.IsTrue(results.All(static result => result.AffectedRows == 1));
        Assert.AreEqual(
          20L,
          (await connection.QueryAsync("SELECT COUNT(*)::int8 FROM batch_values"))[0].Get<long>(0));
    }

    [TestMethod]
    public async Task PreparedBatchFailureKeepsConnectionSynchronized()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
            PipeliningLimit = 8,
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await connection.ExecuteAsync(
          "CREATE TEMP TABLE batch_failure_values (value int4 PRIMARY KEY)");
        await using var statement = await connection.PrepareAsync(
          "INSERT INTO batch_failure_values VALUES ($1::int4)");
        SqlParameters[] batch =
        [
            SqlParameters.Create(1),
            SqlParameters.Create(2),
            SqlParameters.Create(1),
            SqlParameters.Create(3),
        ];

        var exception = await Assert.ThrowsExactlyAsync<PgException>(
          () => statement.ExecuteBatchAsync(batch).AsTask());

        Assert.AreEqual("23505", exception.SqlState);
        var rows = await connection.QueryAsync(
          "SELECT value FROM batch_failure_values ORDER BY value");
        CollectionAssert.AreEqual(
          new[] { 1, 2, 3 },
          rows.Select(static row => row.GetInt32(0)).ToArray());
        Assert.AreEqual(42, (await connection.QueryAsync("SELECT 42::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task DecodesCustomEnumAsStringInTextFormat()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await connection.ExecuteAsync("CREATE TYPE mood AS ENUM ('happy', 'sad')");
        Assert.AreEqual(
          "happy",
          (await connection.QueryAsync("SELECT 'happy'::mood"))[0].Get<string>(0));
    }

    [TestMethod]
    public async Task RejectsNestedTransactionAndHandlesParameterStatus()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await connection.ExecuteAsync("SET application_name = 'apex-runtime-status'");
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
          () => connection.BeginTransactionAsync().AsTask());
        await transaction.RollbackAsync();
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1::int4"))[0].Get<int>(0));
    }

    [TestMethod]
    public async Task DeferredConstraintFailureCompletesTransactionAndReusesConnection()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await connection.ExecuteAsync(
          """
          CREATE TEMP TABLE deferred_parent (name text PRIMARY KEY);
          CREATE TEMP TABLE deferred_child (
            name text PRIMARY KEY,
            parent text REFERENCES deferred_parent(name) DEFERRABLE INITIALLY DEFERRED
          )
          """);
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
          "INSERT INTO deferred_child (name, parent) VALUES ('john', 'mike')");

        var exception = await Assert.ThrowsExactlyAsync<PgException>(
          () => transaction.CommitAsync().AsTask());

        Assert.AreEqual("23503", exception.SqlState);
        Assert.IsTrue(transaction.IsCompleted);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT 42::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task RollsBackAbortedTransactionAndReusesConnection()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };

        await using var connection = await PgClient.ConnectAsync(options);
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsExactlyAsync<PgException>(
          () => connection.QueryAsync("SELECT missing_column").AsTask());
        await Assert.ThrowsExactlyAsync<PgException>(
          () => connection.QueryAsync("SELECT 1::int4").AsTask());

        await transaction.RollbackAsync();

        Assert.IsTrue(transaction.IsCompleted);
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT 42::int4"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task DecodesGeometricTypesInTextAndBinaryFormats()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        const string projection =
          """
          point(1, 2) AS point_value,
          '{1,2,3}'::line AS line_value,
          '[(1,1),(2,2)]'::lseg AS segment_value,
          '((2,2),(1,1))'::box AS box_value,
          '((1,1),(2,1),(2,2))'::path AS closed_path_value,
          '[(1,1),(2,1),(2,2)]'::path AS open_path_value,
          '((1,1),(2,2),(3,1))'::polygon AS polygon_value,
          '<(1,1),3>'::circle AS circle_value
          """;

        await using var connection = await PgClient.ConnectAsync(options);
        AssertGeometricValues(
          (await connection.QueryAsync("SELECT " + projection))[0]);
        AssertGeometricValues(
          (await connection.QueryAsync(
            "SELECT " + projection + ", $1::int4",
            SqlParameters.Create(42)))[0]);
    }

    [TestMethod]
    public async Task EncodesGeometricParameters()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        PgPoint[] pathPoints =
        [
            new PgPoint(1, 1),
            new PgPoint(2, 1),
            new PgPoint(2, 2),
        ];

        await using var connection = await PgClient.ConnectAsync(options);
        var row = (await connection.QueryAsync(
          """
          SELECT
            $1::point AS point_value,
            $2::line AS line_value,
            $3::lseg AS segment_value,
            $4::box AS box_value,
            $5::path AS closed_path_value,
            $6::path AS open_path_value,
            $7::polygon AS polygon_value,
            $8::circle AS circle_value
          """,
          SqlParameters.Create(
            SqlValue.From(new PgPoint(1, 2)),
            SqlValue.From(new PgLine(1, 2, 3)),
            SqlValue.From(new PgLineSegment(new PgPoint(1, 1), new PgPoint(2, 2))),
            SqlValue.From(new PgBox(new PgPoint(2, 2), new PgPoint(1, 1))),
            SqlValue.From(new PgPath(pathPoints, Closed: true)),
            SqlValue.From(new PgPath(pathPoints, Closed: false)),
            SqlValue.From(new PgPolygon(
              [new PgPoint(1, 1), new PgPoint(2, 2), new PgPoint(3, 1)])),
            SqlValue.From(new PgCircle(new PgPoint(1, 1), 3)))))[0];

        AssertGeometricValues(row);
    }

    [TestMethod]
    public async Task EncodesComplexScalarParameters()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        PgTimeWithTimeZone timeWithTimeZone = new(
          new TimeOnly(12, 34, 56, 123, 456),
          TimeSpan.FromHours(2));
        PgInterval interval = new(1, 2, 3, 4, 5, 6, 123456);
        PgInet inet = new(System.Net.IPAddress.Parse("192.0.2.1"), 24);
        PgCidr cidr = new(System.Net.IPAddress.Parse("2001:db8::"), 64);
        PgMoney money = new(12.34m);

        await using var connection = await PgClient.ConnectAsync(options);
        var row = (await connection.QueryAsync(
          "SELECT $1::timetz, $2::interval, $3::inet, $4::cidr, $5::money",
          SqlParameters.Create(
            SqlValue.From(timeWithTimeZone),
            SqlValue.From(interval),
            SqlValue.From(inet),
            SqlValue.From(cidr),
            SqlValue.From(money))))[0];

        Assert.AreEqual(timeWithTimeZone, row.Get<PgTimeWithTimeZone>(0));
        Assert.AreEqual(interval, row.Get<PgInterval>(1));
        Assert.AreEqual(inet, row.Get<PgInet>(2));
        Assert.AreEqual(cidr, row.Get<PgCidr>(3));
        Assert.AreEqual(money, row.Get<PgMoney>(4));
    }

    [TestMethod]
    public async Task DecodesGeometricArraysInTextAndBinaryFormats()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        const string projection =
          """
          ARRAY['(1,1)'::point, '(2,2)'::point] AS point_values,
          ARRAY['{1,2,3}'::line, '{2,3,4}'::line] AS line_values,
          ARRAY['[(1,1),(2,2)]'::lseg, '[(2,2),(3,3)]'::lseg] AS segment_values,
          ARRAY['((2,2),(1,1))'::box, '((3,3),(2,2))'::box] AS box_values,
          ARRAY['((1,1),(2,1),(2,2))'::path, '[(2,2),(3,2),(3,3)]'::path] AS path_values,
          ARRAY['((1,1),(2,2),(3,1))'::polygon, '((0,0),(0,1),(1,0))'::polygon] AS polygon_values,
          ARRAY['<(1,1),1>'::circle, '<(0,0),2>'::circle] AS circle_values
          """;

        await using var connection = await PgClient.ConnectAsync(options);
        AssertGeometricArrayValues(
          (await connection.QueryAsync("SELECT " + projection))[0]);
        AssertGeometricArrayValues(
          (await connection.QueryAsync(
            "SELECT " + projection + ", $1::int4",
            SqlParameters.Create(42)))[0]);
    }

    [TestMethod]
    public async Task EncodesGeometricArrayParameters()
    {
        var container = _container ??
          throw new InvalidOperationException("The PostgreSQL container is not running.");
        PgConnectOptions options = new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
        PgPath[] paths =
        [
            new PgPath(
              [new PgPoint(1, 1), new PgPoint(2, 1), new PgPoint(2, 2)],
              Closed: true),
            new PgPath(
              [new PgPoint(2, 2), new PgPoint(3, 2), new PgPoint(3, 3)],
              Closed: false),
        ];

        await using var connection = await PgClient.ConnectAsync(options);
        var row = (await connection.QueryAsync(
          """
          SELECT
            $1::point[] AS point_values,
            $2::line[] AS line_values,
            $3::lseg[] AS segment_values,
            $4::box[] AS box_values,
            $5::path[] AS path_values,
            $6::polygon[] AS polygon_values,
            $7::circle[] AS circle_values
          """,
          SqlParameters.Create(
            SqlValue.From(new[] { new PgPoint(1, 1), new PgPoint(2, 2) }),
            SqlValue.From(new[] { new PgLine(1, 2, 3), new PgLine(2, 3, 4) }),
            SqlValue.From(new[]
            {
                new PgLineSegment(new PgPoint(1, 1), new PgPoint(2, 2)),
                new PgLineSegment(new PgPoint(2, 2), new PgPoint(3, 3)),
            }),
            SqlValue.From(new[]
            {
                new PgBox(new PgPoint(2, 2), new PgPoint(1, 1)),
                new PgBox(new PgPoint(3, 3), new PgPoint(2, 2)),
            }),
            SqlValue.From(paths),
            SqlValue.From(new[]
            {
                new PgPolygon([new PgPoint(1, 1), new PgPoint(2, 2), new PgPoint(3, 1)]),
                new PgPolygon([new PgPoint(0, 0), new PgPoint(0, 1), new PgPoint(1, 0)]),
            }),
            SqlValue.From(new[]
            {
                new PgCircle(new PgPoint(1, 1), 1),
                new PgCircle(new PgPoint(0, 0), 2),
            }))))[0];

        AssertGeometricArrayValues(row);
    }

    private static void AssertTypeValues(SqlRow row)
    {
        Assert.IsTrue(row.GetBoolean("boolean_value"));
        Assert.IsTrue(row.Get<bool>("boolean_value"));
        Assert.AreEqual((short)2, row.GetInt16("int2_value"));
        Assert.AreEqual((short)2, row.Get<short>("int2_value"));
        Assert.AreEqual(3, row.GetInt32("int4_value"));
        Assert.AreEqual(3, row.Get<int>("int4_value"));
        Assert.AreEqual(4L, row.GetInt64("int8_value"));
        Assert.AreEqual(4L, row.Get<long>("int8_value"));
        Assert.AreEqual(1.5f, row.GetFloat("float4_value"));
        Assert.AreEqual(1.5f, row.Get<float>("float4_value"));
        Assert.AreEqual(2.5d, row.GetDouble("float8_value"));
        Assert.AreEqual(2.5d, row.Get<double>("float8_value"));
        Assert.AreEqual(
          12345678901234567890.1234m,
          row.GetDecimal("numeric_value"));
        Assert.AreEqual(
          12345678901234567890.1234m,
          row.Get<decimal>("numeric_value"));
        Assert.AreEqual(
          "12345678901234567890.1234",
          row.Get<PgNumeric>("numeric_value").ToString());
        Assert.AreEqual(
          Guid.Parse("12345678-1234-5678-9012-123456789abc"),
          row.GetGuid("uuid_value"));
        Assert.AreEqual(
          Guid.Parse("12345678-1234-5678-9012-123456789abc"),
          row.Get<Guid>("uuid_value"));
        Assert.AreEqual(
          new DateOnly(2026, 8, 14),
          row.GetDateOnly("date_value"));
        Assert.AreEqual(
          new DateOnly(2026, 8, 14),
          row.Get<DateOnly>("date_value"));
        Assert.AreEqual(
          new TimeOnly(12, 34, 56, 123, 456),
          row.GetTimeOnly("time_value"));
        Assert.AreEqual(
          new TimeOnly(12, 34, 56, 123, 456),
          row.Get<TimeOnly>("time_value"));
        Assert.AreEqual(
          new DateTime(
            2026,
            8,
            14,
            12,
            34,
            56,
            123,
            456,
            DateTimeKind.Unspecified),
          row.GetDateTime("timestamp_value"));
        Assert.AreEqual(
          new DateTime(
            2026,
            8,
            14,
            12,
            34,
            56,
            123,
            456,
            DateTimeKind.Unspecified),
          row.Get<DateTime>("timestamp_value"));
        Assert.AreEqual(
          new DateTimeOffset(
            2026,
            8,
            14,
            12,
            34,
            56,
            123,
            456,
            TimeSpan.Zero),
          row.GetDateTimeOffset("timestamptz_value"));
        Assert.AreEqual(
          new DateTimeOffset(
            2026,
            8,
            14,
            12,
            34,
            56,
            123,
            456,
            TimeSpan.Zero),
          row.Get<DateTimeOffset>("timestamptz_value"));
        Assert.AreEqual(TimeSpan.FromHours(2), row.Get<PgTimeWithTimeZone>("timetz_value").Offset);
        Assert.AreEqual(
          new PgInterval(1, 2, 3, 4, 5, 6, 123456),
          row.Get<PgInterval>("interval_value"));
        CollectionAssert.AreEqual(
          new byte[] { 0, 1, 254, 255 },
          row.GetBytes("bytea_value"));
        CollectionAssert.AreEqual(
          new byte[] { 0, 1, 254, 255 },
          row.Get<byte[]>("bytea_value"));
        CollectionAssert.AreEqual(
          new byte[] { 0, 1, 254, 255 },
          row.Get<ReadOnlyMemory<byte>>("bytea_value").ToArray());
        Assert.IsTrue(row.Get<System.Text.Json.JsonElement>("json_value").GetProperty("ok").GetBoolean());
        Assert.AreEqual(new PgPoint(1.5, -2.25), row.Get<PgPoint>("point_value"));
        Assert.AreEqual(24, row.Get<PgInet>("inet_value").PrefixLength);
        Assert.AreEqual(64, row.Get<PgCidr>("cidr_value").PrefixLength);
        Assert.AreEqual(12.34m, row.Get<PgMoney>("money_value").Value);
        CollectionAssert.AreEqual(
          new int?[] { 1, null, 3 },
          row.GetArray<int?>("array_value"));
    }

    private static void AssertGeometricValues(SqlRow row)
    {
        Assert.AreEqual(new PgPoint(1, 2), row.Get<PgPoint>("point_value"));
        Assert.AreEqual(new PgLine(1, 2, 3), row.Get<PgLine>("line_value"));
        Assert.AreEqual(
          new PgLineSegment(new PgPoint(1, 1), new PgPoint(2, 2)),
          row.Get<PgLineSegment>("segment_value"));
        Assert.AreEqual(
          new PgBox(new PgPoint(2, 2), new PgPoint(1, 1)),
          row.Get<PgBox>("box_value"));

        var closedPath = row.Get<PgPath>("closed_path_value");
        Assert.IsTrue(closedPath.Closed);
        CollectionAssert.AreEqual(
          new[] { new PgPoint(1, 1), new PgPoint(2, 1), new PgPoint(2, 2) },
          closedPath.Points.ToArray());
        var openPath = row.Get<PgPath>("open_path_value");
        Assert.IsFalse(openPath.Closed);
        CollectionAssert.AreEqual(
          new[] { new PgPoint(1, 1), new PgPoint(2, 1), new PgPoint(2, 2) },
          openPath.Points.ToArray());
        CollectionAssert.AreEqual(
          new[] { new PgPoint(1, 1), new PgPoint(2, 2), new PgPoint(3, 1) },
          row.Get<PgPolygon>("polygon_value").Points.ToArray());
        Assert.AreEqual(
          new PgCircle(new PgPoint(1, 1), 3),
          row.Get<PgCircle>("circle_value"));
    }

    private static void AssertGeometricArrayValues(SqlRow row)
    {
        CollectionAssert.AreEqual(
          new[] { new PgPoint(1, 1), new PgPoint(2, 2) },
          row.GetArray<PgPoint>("point_values"));
        CollectionAssert.AreEqual(
          new[] { new PgLine(1, 2, 3), new PgLine(2, 3, 4) },
          row.GetArray<PgLine>("line_values"));
        CollectionAssert.AreEqual(
          new[]
          {
              new PgLineSegment(new PgPoint(1, 1), new PgPoint(2, 2)),
              new PgLineSegment(new PgPoint(2, 2), new PgPoint(3, 3)),
          },
          row.GetArray<PgLineSegment>("segment_values"));
        CollectionAssert.AreEqual(
          new[]
          {
              new PgBox(new PgPoint(2, 2), new PgPoint(1, 1)),
              new PgBox(new PgPoint(3, 3), new PgPoint(2, 2)),
          },
          row.GetArray<PgBox>("box_values"));

        var paths = row.GetArray<PgPath>("path_values")!;
        var closedPath = paths[0] ?? throw new AssertFailedException("Expected a closed path.");
        var openPath = paths[1] ?? throw new AssertFailedException("Expected an open path.");
        Assert.IsTrue(closedPath.Closed);
        Assert.IsFalse(openPath.Closed);
        CollectionAssert.AreEqual(
          new[] { new PgPoint(1, 1), new PgPoint(2, 1), new PgPoint(2, 2) },
          closedPath.Points.ToArray());
        CollectionAssert.AreEqual(
          new[] { new PgPoint(2, 2), new PgPoint(3, 2), new PgPoint(3, 3) },
          openPath.Points.ToArray());

        var polygons = row.GetArray<PgPolygon>("polygon_values")!;
        var firstPolygon = polygons[0] ?? throw new AssertFailedException("Expected a polygon.");
        var secondPolygon = polygons[1] ?? throw new AssertFailedException("Expected a polygon.");
        CollectionAssert.AreEqual(
          new[] { new PgPoint(1, 1), new PgPoint(2, 2), new PgPoint(3, 1) },
          firstPolygon.Points.ToArray());
        CollectionAssert.AreEqual(
          new[] { new PgPoint(0, 0), new PgPoint(0, 1), new PgPoint(1, 0) },
          secondPolygon.Points.ToArray());
        CollectionAssert.AreEqual(
          new[]
          {
              new PgCircle(new PgPoint(1, 1), 1),
              new PgCircle(new PgPoint(0, 0), 2),
          },
          row.GetArray<PgCircle>("circle_values"));
    }

    private static async ValueTask<PgNotification> NextNotificationAsync(
        IAsyncEnumerable<PgNotification> notifications,
        TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        await foreach (var notification in notifications.WithCancellation(cancellation.Token))
        {
            return notification;
        }

        throw new InvalidOperationException("The PostgreSQL notification stream completed.");
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
