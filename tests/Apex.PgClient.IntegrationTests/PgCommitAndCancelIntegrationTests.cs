using System.Transactions;
using Testcontainers.PostgreSql;

namespace Apex.PgClient.IntegrationTests;

[TestClass]
public sealed class PgCommitAndCancelIntegrationTests
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
    public async Task AmbientCommitFailureAbortsTheTransactionScope()
    {
        await using var connection = await PgClient.ConnectAsync(CreateOptions());
        await connection.ExecuteAsync(
            "CREATE TEMP TABLE deferred_values (value int UNIQUE DEFERRABLE INITIALLY DEFERRED)");

        // PostgreSQL alone: single-phase commit, so the COMMIT result is the scope's outcome.
        var aborted = await CompleteScopeAsync(
            connection,
            "INSERT INTO deferred_values VALUES (1), (1)",
            other: null);
        Assert.AreEqual("23505", ((PgException)aborted!.InnerException!).SqlState);

        // With another resource: COMMIT runs in Prepare and its failure rolls the other back.
        RecordingEnlistment other = new();
        aborted = await CompleteScopeAsync(
            connection,
            "INSERT INTO deferred_values VALUES (2), (2)",
            other);
        Assert.AreEqual("23505", ((PgException)aborted!.InnerException!).SqlState);
        Assert.AreEqual("rollback", other.Outcome);

        // A transaction that failed earlier answers COMMIT with ROLLBACK and no error.
        aborted = await CompleteScopeAsync(
            connection,
            "INSERT INTO deferred_values VALUES (3); SELECT 1/0",
            other: null,
            ignoreStatementError: true);
        Assert.IsInstanceOfType<TransactionException>(aborted!.InnerException);

        Assert.AreEqual(
            0L,
            (await connection.QueryAsync("SELECT count(*)::int8 FROM deferred_values"))[0].Get<long>(0));

        // And a successful COMMIT in Prepare still lets every resource commit.
        other = new RecordingEnlistment();
        Assert.IsNull(await CompleteScopeAsync(
            connection,
            "INSERT INTO deferred_values VALUES (4)",
            other));
        Assert.AreEqual("commit", other.Outcome);
        Assert.AreEqual(
            1L,
            (await connection.QueryAsync("SELECT count(*)::int8 FROM deferred_values"))[0].Get<long>(0));
    }

    // A token that fires as command A finishes must cancel A or nothing, never the next command.
    [TestMethod]
    public async Task LateCancellationNeverCancelsTheNextCommand()
    {
        await using var connection = await PgClient.ConnectAsync(CreateOptions());
        var canceledFirst = 0;
        for (var i = 0; i < 200; i++)
        {
            using CancellationTokenSource cancellation = new();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(1 + (i % 5)));
            try
            {
                await connection.QueryAsync("SELECT pg_sleep(0.003)", cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceledFirst++;
            }

            // Throws PgException 57014 if A's cancel request lands here.
            var rows = await connection.QueryAsync("SELECT 1::int4 FROM pg_sleep(0.01)");
            Assert.AreEqual(1, rows[0].GetInt32(0));
        }

        Assert.IsGreaterThan(0, canceledFirst, "No iteration exercised a cancellation.");
    }

    private static async Task<TransactionAbortedException?> CompleteScopeAsync(
        PgConnection connection,
        string sql,
        RecordingEnlistment? other,
        bool ignoreStatementError = false)
    {
        try
        {
            using TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled);
            await connection.EnlistTransactionAsync(Transaction.Current!);
            if (other is not null)
            {
                Transaction.Current!.EnlistVolatile(other, EnlistmentOptions.None);
            }

            try
            {
                await connection.ExecuteAsync(sql);
            }
            catch (PgException) when (ignoreStatementError)
            {
            }

            scope.Complete();
        }
        catch (TransactionAbortedException exception)
        {
            return exception;
        }

        return null;
    }

    private PgConnectOptions CreateOptions()
    {
        var container = _container ??
            throw new InvalidOperationException("The PostgreSQL container is not running.");
        return new PgConnectOptions
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
    }

    private sealed class RecordingEnlistment : IEnlistmentNotification
    {
        public string? Outcome { get; private set; }

        public void Prepare(PreparingEnlistment preparingEnlistment) =>
            preparingEnlistment.Prepared();

        public void Commit(Enlistment enlistment)
        {
            Outcome = "commit";
            enlistment.Done();
        }

        public void Rollback(Enlistment enlistment)
        {
            Outcome = "rollback";
            enlistment.Done();
        }

        public void InDoubt(Enlistment enlistment)
        {
            Outcome = "indoubt";
            enlistment.Done();
        }
    }
}
