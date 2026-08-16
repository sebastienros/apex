using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Apex.SqlClient.SpecificationTests;

[TestClass]
public abstract class SqlClientSpecificationTests
{
    protected abstract ValueTask<ISqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default);

    protected abstract ValueTask<ISqlConnection> OpenConnectionAsync(
      string host,
      int port,
      int reconnectAttempts,
      TimeSpan reconnectInterval,
      CancellationToken cancellationToken = default);

    protected abstract ISqlPool CreatePool(int maximumSize = 4);

    protected abstract ISqlPool CreatePool(
      string host,
      int port,
      int reconnectAttempts,
      TimeSpan reconnectInterval,
      int maximumSize = 4);

    protected abstract string ServerHost { get; }

    protected abstract int ServerPort { get; }

    protected abstract string ParameterizedScalarSql { get; }

    protected abstract string CreateTemporaryTableSql { get; }

    protected abstract string InsertTemporaryValueSql { get; }

    protected abstract string CountTemporaryValuesSql { get; }

    protected abstract string SequenceSql { get; }

    protected abstract string LongRunningSql { get; }

    protected abstract string DiagnosticSystemName { get; }

    protected virtual bool CoercesInvalidIntegerParameters => false;

    protected virtual string CountRowsSql(string tableName) =>
      $"SELECT CAST(COUNT(*) AS BIGINT) FROM {tableName}";

    protected virtual string CountUncommittedRowsSql(string tableName) =>
      CountRowsSql(tableName);

    [TestMethod]
    public async Task ConnectsAndQueriesScalar()
    {
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync("SELECT 1");

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(1, rows[0].Get<int>(0));
    }

    [TestMethod]
    public async Task ExecutesParameterizedQuery()
    {
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync(
          ParameterizedScalarSql,
          SqlParameters.Create(42));

        Assert.AreEqual(42, rows[0].Get<int>(0));
    }

    [TestMethod]
    public async Task RollsBackTransactionOnDispose()
    {
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(CreateTemporaryTableSql);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(InsertTemporaryValueSql, SqlParameters.Create(1));
        }

        var rows = await connection.QueryAsync(CountTemporaryValuesSql);
        Assert.AreEqual(0L, rows[0].Get<long>(0));
    }

      [TestMethod]
      public async Task CommitsExplicitTransaction()
      {
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(CreateTemporaryTableSql);
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(InsertTemporaryValueSql, SqlParameters.Create(1));

        await transaction.CommitAsync();

        Assert.IsTrue(transaction.IsCompleted);
        var rows = await connection.QueryAsync(CountTemporaryValuesSql);
        Assert.AreEqual(1L, rows[0].Get<long>(0));
      }

      [TestMethod]
      public async Task RollsBackExplicitTransaction()
      {
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(CreateTemporaryTableSql);
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(InsertTemporaryValueSql, SqlParameters.Create(1));

        await transaction.RollbackAsync();

        Assert.IsTrue(transaction.IsCompleted);
        var rows = await connection.QueryAsync(CountTemporaryValuesSql);
        Assert.AreEqual(0L, rows[0].Get<long>(0));
      }

    [TestMethod]
    public async Task TransactionChangesBecomeVisibleOnlyAfterCommit()
    {
        var tableName = "apex_visibility_" + Guid.NewGuid().ToString("N");
        await using var writer = await OpenConnectionAsync();
        await using var observer = await OpenConnectionAsync();
        await writer.ExecuteAsync($"CREATE TABLE {tableName} (value int NOT NULL)");
        try
        {
            await using var transaction = await writer.BeginTransactionAsync();
            await writer.ExecuteAsync($"INSERT INTO {tableName} VALUES (1)");

            Assert.AreEqual(
              1L,
              (await writer.QueryAsync(CountRowsSql(tableName)))[0].Get<long>(0));
            Assert.AreEqual(
              0L,
              (await observer.QueryAsync(CountUncommittedRowsSql(tableName)))[0].Get<long>(0));

            await transaction.CommitAsync();

            Assert.AreEqual(
              1L,
              (await observer.QueryAsync(CountRowsSql(tableName)))[0].Get<long>(0));
        }
        finally
        {
            await writer.ExecuteAsync($"DROP TABLE {tableName}");
        }
    }

    [TestMethod]
    public async Task PoolReleasesConnectionAfterWithTransaction()
    {
        await using var pool = CreatePool(maximumSize: 1);

        var value = await pool.WithTransactionAsync(
          async (connection, _) =>
          {
              var rows = await connection.QueryAsync("SELECT 1");
              return rows[0].Get<int>(0);
          });
        await using var lease = await pool.GetConnectionAsync();

        Assert.AreEqual(1, value);
        Assert.AreEqual(1, (await lease.QueryAsync("SELECT 1"))[0].Get<int>(0));
        Assert.AreEqual(1, pool.Size);
    }

    [TestMethod]
    public async Task ExecutesPreparedBatch()
    {
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(CreateTemporaryTableSql);
        await using var statement =
          await connection.PrepareAsync(InsertTemporaryValueSql);
        var batch = Enumerable.Range(0, 16)
          .Select(static value => SqlParameters.Create(value))
          .ToArray();

        var results = await statement.ExecuteBatchAsync(batch);

        Assert.AreEqual(16, results.Count);
        Assert.IsTrue(results.All(static result => result.AffectedRows == 1));
    }

      [TestMethod]
      public async Task ExecutesEmptyPreparedBatch()
      {
        await using var connection = await OpenConnectionAsync();
        await using var statement =
          await connection.PrepareAsync(ParameterizedScalarSql);

        var results = await statement.ExecuteBatchAsync([]);

        Assert.HasCount(0, results);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
      }

      [TestMethod]
      public async Task PrepareSyntaxFailureLeavesConnectionReusable()
      {
        await using var connection = await OpenConnectionAsync();
        ISqlPreparedStatement? statement = null;
        Exception? failure = null;
        try
        {
          statement = await connection.PrepareAsync("SELECT * FROM");
          _ = await statement.QueryAsync();
        }
        catch (Exception exception)
        {
          failure = exception;
        }
        finally
        {
          if (statement is not null)
          {
            await statement.DisposeAsync();
          }
        }

        Assert.IsNotNull(failure);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
      }

      [TestMethod]
      public async Task MissingPreparedParameterLeavesConnectionReusable()
      {
        await using var connection = await OpenConnectionAsync();
        await using var statement = await connection.PrepareAsync(ParameterizedScalarSql);

        await Assert.ThrowsAsync<Exception>(() => statement.QueryAsync().AsTask());

        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
      }

  [TestMethod]
  public async Task PreparedStatementAcceptsNullBeforeTypedValue()
  {
    await using var connection = await OpenConnectionAsync();
    await using var statement = await connection.PrepareAsync(ParameterizedScalarSql);

    var nullRows = await statement.QueryAsync(SqlParameters.Create(SqlValue.Null));
    var valueRows = await statement.QueryAsync(SqlParameters.Create(42));

    Assert.IsTrue(nullRows[0].IsNull(0));
    Assert.AreEqual(42, valueRows[0].Get<int>(0));
  }

  [TestMethod]
    public async Task HandlesExtraPreparedParameterAndLeavesConnectionReusable()
  {
    await using var connection = await OpenConnectionAsync();
    await using var statement = await connection.PrepareAsync(ParameterizedScalarSql);
        Assert.AreEqual(41, (await statement.QueryAsync(SqlParameters.Create(41)))[0].Get<int>(0));

        await Assert.ThrowsAsync<Exception>(
          () => statement.QueryAsync(SqlParameters.Create(1, 2)).AsTask());

    Assert.AreEqual(42, (await statement.QueryAsync(SqlParameters.Create(42)))[0].Get<int>(0));
  }

  [TestMethod]
    public async Task PreparedStatementAcceptsParameterTypeChanges()
  {
    await using var connection = await OpenConnectionAsync();
    await using var statement = await connection.PrepareAsync(ParameterizedScalarSql);
    Assert.AreEqual(42, (await statement.QueryAsync(SqlParameters.Create(42)))[0].Get<int>(0));

        Assert.AreEqual(
          43,
          (await statement.QueryAsync(SqlParameters.Create("43")))[0].Get<int>(0));
        Assert.AreEqual(44, (await statement.QueryAsync(SqlParameters.Create(44)))[0].Get<int>(0));
  }

    [TestMethod]
    public async Task HandlesIncompatibleIntegerParameterAndRemainsReusable()
    {
        await using var connection = await OpenConnectionAsync();
        await using var statement = await connection.PrepareAsync(ParameterizedScalarSql);
        Assert.AreEqual(41, (await statement.QueryAsync(SqlParameters.Create(41)))[0].Get<int>(0));

        if (CoercesInvalidIntegerParameters)
        {
            Assert.AreEqual(
              0,
              (await statement.QueryAsync(
                SqlParameters.Create("not-an-integer")))[0].Get<int>(0));
        }
        else
        {
            await Assert.ThrowsAsync<Exception>(
              () => statement.QueryAsync(
                SqlParameters.Create("not-an-integer")).AsTask());
        }

        Assert.AreEqual(42, (await statement.QueryAsync(SqlParameters.Create(42)))[0].Get<int>(0));
    }

      [TestMethod]
      public async Task CursorCanBeDisposedTwice()
      {
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var statement = await connection.PrepareAsync(SequenceSql);
        var cursor = await statement.OpenCursorAsync(fetchSize: 3);
        var rows = await cursor.ReadAsync(3);

        await cursor.DisposeAsync();
        await cursor.DisposeAsync();

        Assert.HasCount(3, rows);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
      }

    [TestMethod]
    public async Task StreamsWithFetchSize()
    {
        await using var connection = await OpenConnectionAsync();
        List<int> values = [];
        await foreach (var row in connection.StreamAsync(SequenceSql, fetchSize: 3))
        {
            values.Add(row.Get<int>(0));
        }

        CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(), values);
    }

    [TestMethod]
    public async Task CancellationLeavesConnectionReusable()
    {
        await using var connection = await OpenConnectionAsync();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(
          () => connection.QueryAsync(LongRunningSql, cancellation.Token).AsTask());

        var rows = await connection.QueryAsync("SELECT 1");
        Assert.AreEqual(1, rows[0].Get<int>(0));
    }

    [TestMethod]
    public async Task PoolServesConcurrentQueries()
    {
        await using var pool = CreatePool();
        var queries = Enumerable.Range(0, 32)
          .Select(_ => pool.QueryAsync("SELECT 1").AsTask())
          .ToArray();

        var results = await Task.WhenAll(queries);

        Assert.IsTrue(results.All(static rows => rows[0].Get<int>(0) == 1));
        Assert.IsLessThanOrEqualTo(4, pool.Size);
    }

    [TestMethod]
    public async Task DirectConnectionRetriesTransientHandshakeFailures()
    {
        await using FaultInjectingTcpProxy proxy = new(
          ServerHost,
          ServerPort,
          connectionsToDrop: 2);

        await using var connection = await OpenConnectionAsync(
          "127.0.0.1",
          proxy.Port,
          reconnectAttempts: 2,
          reconnectInterval: TimeSpan.FromMilliseconds(25));

        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
        Assert.AreEqual(3, proxy.AcceptedConnections);
    }

    [TestMethod]
    public async Task PoolRetriesTransientHandshakeFailures()
    {
        await using FaultInjectingTcpProxy proxy = new(
          ServerHost,
          ServerPort,
          connectionsToDrop: 2);
        await using var pool = CreatePool(
          "127.0.0.1",
          proxy.Port,
          reconnectAttempts: 2,
          reconnectInterval: TimeSpan.FromMilliseconds(25),
          maximumSize: 1);

        var rows = await pool.QueryAsync("SELECT 1");

        Assert.AreEqual(1, rows[0].Get<int>(0));
        Assert.AreEqual(3, proxy.AcceptedConnections);
        Assert.AreEqual(1, pool.Size);
    }

    [TestMethod]
    public async Task PoolReplacesFailedConnectionForQueuedAcquisition()
    {
        await using FaultInjectingTcpProxy proxy = new(
          ServerHost,
          ServerPort,
          connectionsToDrop: 0);
        await using var pool = CreatePool(
          "127.0.0.1",
          proxy.Port,
          reconnectAttempts: 0,
          reconnectInterval: TimeSpan.Zero,
          maximumSize: 1);
        var first = await pool.GetConnectionAsync();
        Assert.AreEqual(1, (await first.QueryAsync("SELECT 1"))[0].Get<int>(0));
        var queued = pool.GetConnectionAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(queued.IsCompleted);

        proxy.CloseActiveConnections();
        await Assert.ThrowsAsync<Exception>(
          () => first.QueryAsync("SELECT 1").AsTask());
        await first.DisposeAsync();
        await using var replacement = await queued;

        Assert.AreEqual(1, (await replacement.QueryAsync("SELECT 1"))[0].Get<int>(0));
        Assert.AreEqual(2, proxy.AcceptedConnections);
        Assert.AreEqual(1, pool.Size);
    }

    [TestMethod]
    public async Task MapsAndCollectsRows()
    {
        await using var connection = await OpenConnectionAsync();
        var mapped = await connection.QueryMappedAsync(
          SequenceSql,
          static row => row.Get<int>(0));
        var sum = await connection.QueryCollectedAsync(
          SequenceSql,
          static rows => rows.Sum(static row => row.Get<int>(0)));

        CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(), mapped.ToArray());
        Assert.AreEqual(55, sum);
    }

    [TestMethod]
    public async Task MapperAndCollectorFailuresLeaveConnectionReusable()
    {
        await using var connection = await OpenConnectionAsync();
        var mapperFailure = new InvalidOperationException("mapper failure");
        var collectorFailure = new InvalidOperationException("collector failure");

        var actualMapperFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
          () => connection.QueryMappedAsync<int>(SequenceSql, _ => throw mapperFailure).AsTask());
        var actualCollectorFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
          () => connection.QueryCollectedAsync<int>(SequenceSql, _ => throw collectorFailure).AsTask());

        Assert.AreSame(mapperFailure, actualMapperFailure);
        Assert.AreSame(collectorFailure, actualCollectorFailure);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
    }

      [TestMethod]
      public async Task StreamMapperFailureLeavesConnectionReusable()
      {
        await using var connection = await OpenConnectionAsync();
        var mapperFailure = new InvalidOperationException("stream mapper failure");

        var actualFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
          async () =>
          {
            await foreach (var _ in connection.StreamMappedAsync<int>(
                     SequenceSql,
                     _ => throw mapperFailure,
                     fetchSize: 3))
            {
            }
          });

        Assert.AreSame(mapperFailure, actualFailure);
        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
      }

      [TestMethod]
      public async Task PreparedStreamCancellationLeavesConnectionReusable()
      {
        await using var connection = await OpenConnectionAsync();
        await using var statement = await connection.PrepareAsync(LongRunningSql);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(
          async () =>
          {
            await foreach (var _ in statement.StreamAsync(
                     fetchSize: 1,
                     cancellationToken: cancellation.Token))
            {
            }
          });

        Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
      }

  [TestMethod]
  public async Task PreparedStreamFailureLeavesConnectionReusable()
  {
    await using var connection = await OpenConnectionAsync();
    await using var statement = await connection.PrepareAsync(ParameterizedScalarSql);

    await Assert.ThrowsAsync<Exception>(
      async () =>
      {
        await foreach (var _ in statement.StreamAsync(fetchSize: 1))
        {
        }
      });

    Assert.AreEqual(42, (await statement.QueryAsync(SqlParameters.Create(42)))[0].Get<int>(0));
    Assert.AreEqual(1, (await connection.QueryAsync("SELECT 1"))[0].Get<int>(0));
  }

  [TestMethod]
  public async Task DisposalDrainsSuccessfulInFlightQuery()
  {
    var connection = await OpenConnectionAsync();
    var pending = connection.QueryAsync(SequenceSql).AsTask();

    await connection.DisposeAsync();
    var rows = await pending;

    Assert.HasCount(10, rows);
    CollectionAssert.AreEqual(
      Enumerable.Range(1, 10).ToArray(),
      rows.Select(static row => row.Get<int>(0)).ToArray());
  }

  [TestMethod]
  public async Task DisposalDrainsFailingInFlightQuery()
  {
    var connection = await OpenConnectionAsync();
    var pending = connection.QueryAsync("SELECT missing_column").AsTask();

    var disposal = connection.DisposeAsync().AsTask();
    await Assert.ThrowsAsync<Exception>(() => pending);
    await disposal;
  }

    [TestMethod]
    public async Task DisposalDrainsQueriesAlreadyQueuedBehindInFlightWork()
    {
        var connection = await OpenConnectionAsync();
        var first = connection.QueryAsync(SequenceSql).AsTask();
        var second = connection.QueryAsync("SELECT 1").AsTask();

        await connection.DisposeAsync();
        var firstRows = await first;
        var secondRows = await second;

        Assert.HasCount(10, firstRows);
        Assert.AreEqual(1, secondRows[0].Get<int>(0));
    }

    [TestMethod]
    public async Task OperationsAfterDisposalFailImmediately()
    {
        var connection = await OpenConnectionAsync();
        await connection.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
          () => connection.QueryAsync("SELECT 1").AsTask());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
          () => connection.PrepareAsync(ParameterizedScalarSql).AsTask());
    }

  [TestMethod]
  public async Task EmitsActivitiesAndMetricsForSuccessfulAndFailedQueries()
  {
    ConcurrentQueue<Activity> activities = new();
    var durationCount = 0;
    var errorCount = 0;
    using ActivityListener activityListener = new()
    {
      ShouldListenTo = static source => source.Name == "Apex.SqlClient",
      Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = activities.Enqueue,
    };
    ActivitySource.AddActivityListener(activityListener);
    using MeterListener meterListener = new();
    meterListener.InstrumentPublished = static (instrument, listener) =>
    {
      if (instrument.Meter.Name == "Apex.SqlClient")
      {
        listener.EnableMeasurementEvents(instrument);
      }
    };
    meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
    {
      if (instrument.Name == "db.client.operation.duration" &&
        HasTag(tags, "db.system.name", DiagnosticSystemName))
      {
        Interlocked.Increment(ref durationCount);
      }
    });
    meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
    {
      if (instrument.Name == "db.client.operation.errors" &&
        HasTag(tags, "db.system.name", DiagnosticSystemName))
      {
        Interlocked.Increment(ref errorCount);
      }
    });
    meterListener.Start();
    using Activity scope = new("sql-client-diagnostics-test");
    scope.Start();

    await using var connection = await OpenConnectionAsync();
    _ = await connection.QueryAsync("SELECT 1");
    await using (var statement = await connection.PrepareAsync(ParameterizedScalarSql))
    {
      _ = await statement.QueryAsync(SqlParameters.Create(42));
    }

    await Assert.ThrowsAsync<Exception>(
      () => connection.QueryAsync("SELECT missing_column").AsTask());
    await using var pool = CreatePool();
    _ = await pool.QueryAsync("SELECT 1");

    var matching = activities.Where(activity =>
      activity.ParentId == scope.Id &&
      Equals(activity.GetTagItem("db.system.name"), DiagnosticSystemName)).ToArray();
    Assert.IsGreaterThanOrEqualTo(4, matching.Length);
    Assert.IsTrue(matching.All(static activity => activity.Kind == ActivityKind.Client));
    Assert.IsTrue(matching.All(static activity =>
      Equals(activity.GetTagItem("db.operation.name"), "SELECT")));
    Assert.IsTrue(matching.Any(static activity => activity.Status == ActivityStatusCode.Error));
    Assert.IsGreaterThanOrEqualTo(4, durationCount);
    Assert.IsGreaterThanOrEqualTo(1, errorCount);
  }

  private static bool HasTag(
    ReadOnlySpan<KeyValuePair<string, object?>> tags,
    string name,
    string value)
  {
    foreach (var tag in tags)
    {
      if (tag.Key == name && Equals(tag.Value, value))
      {
        return true;
      }
    }

    return false;
  }
}
