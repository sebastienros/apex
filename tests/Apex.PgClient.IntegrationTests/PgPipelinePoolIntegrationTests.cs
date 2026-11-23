using System.Diagnostics;
using Apex.SqlClient;
using Testcontainers.PostgreSql;

namespace Apex.PgClient.IntegrationTests;

[TestClass]
public sealed class PgPipelinePoolIntegrationTests
{
    private const int PoolSize = 3;
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
    public async Task RecoversAfterABackendIsTerminated()
    {
        var options = CreateOptions();
        await using var pool = await PgPipelinePool.CreateAsync(
            options,
            new SqlPipelinePoolOptions { ConnectionCount = PoolSize });
        await using var admin = await PgClient.ConnectAsync(options);
        await using var statement = await pool.PrepareAsync(
            "SELECT pg_backend_pid() AS pid, $1::int4 AS value");

        var before = await ReadPidsAsync(pool, PoolSize);
        Assert.HasCount(PoolSize, before.Distinct());
        var victim = before[0];

        var terminated = await admin.QueryAsync(
            "SELECT pg_terminate_backend($1::int4)",
            SqlParameters.Create(victim));
        Assert.IsTrue(terminated[0].Get<bool>(0));
        await WaitUntilGoneAsync(admin, victim);

        // The pool learns a backend is gone only when a command on it fails, so expect a
        // failure or two before dispatch routes around the dead slot and the replacement lands.
        var failures = 0;
        var consecutive = 0;
        var recent = new Queue<int>();
        var deadline = Stopwatch.StartNew();
        while (consecutive < 2 * PoolSize ||
               recent.Distinct().Count() < PoolSize ||
               recent.Contains(victim))
        {
            Assert.IsLessThan(TimeSpan.FromSeconds(30), deadline.Elapsed, "The pool did not recover.");
            try
            {
                var rows = await pool.QueryAsync("SELECT pg_backend_pid()");
                consecutive++;
                recent.Enqueue(rows[0].Get<int>(0));
                if (recent.Count > PoolSize)
                {
                    recent.Dequeue();
                }
            }
            catch (Exception exception) when (exception is PgException or IOException)
            {
                failures++;
                consecutive = 0;
                recent.Clear();
            }

            if (consecutive == 0 || recent.Contains(victim))
            {
                await Task.Delay(50);
            }
        }

        Assert.IsGreaterThanOrEqualTo(1, failures);
        Assert.IsLessThanOrEqualTo(PoolSize, failures);

        // The pool-wide statement was prepared again on the replacement connection, so it now
        // runs on every slot without the terminal error of the killed backend.
        HashSet<int> statementPids = [];
        for (var i = 0; i < 2 * PoolSize; i++)
        {
            var rows = await statement.QueryAsync(SqlParameters.Create(i));
            Assert.AreEqual(i, rows[0].Get<int>("value"));
            statementPids.Add(rows[0].Get<int>("pid"));
        }

        Assert.HasCount(PoolSize, statementPids);
        Assert.DoesNotContain(victim, statementPids);
        CollectionAssert.AreEquivalent(recent.ToArray(), statementPids.ToArray());
    }

    private PgConnectOptions CreateOptions()
    {
        var container = _container ??
            throw new InvalidOperationException("The PostgreSQL container is not running.");
        return new()
        {
            Host = container.Hostname,
            Port = container.GetMappedPublicPort(5432),
            Database = "db",
            Username = "user",
            Password = "pass",
        };
    }

    private static async Task<int[]> ReadPidsAsync(PgPipelinePool pool, int count)
    {
        var pids = new int[count];
        for (var i = 0; i < count; i++)
        {
            var rows = await pool.QueryAsync("SELECT pg_backend_pid()");
            pids[i] = rows[0].Get<int>(0);
        }

        return pids;
    }

    private static async Task WaitUntilGoneAsync(PgConnection admin, int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var rows = await admin.QueryAsync(
                "SELECT COUNT(*)::int4 FROM pg_stat_activity WHERE pid = $1::int4",
                SqlParameters.Create(pid));
            if (rows[0].Get<int>(0) == 0)
            {
                return;
            }

            Assert.IsLessThan(TimeSpan.FromSeconds(10), deadline.Elapsed, "The backend did not exit.");
            await Task.Delay(50);
        }
    }
}
