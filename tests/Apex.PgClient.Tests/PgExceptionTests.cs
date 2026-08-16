namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgExceptionTests
{
    [TestMethod]
    public void ExposesPostgreSqlErrorFields()
    {
        Dictionary<char, string> fields = new()
        {
            ['S'] = "ERROR",
            ['V'] = "ERROR",
            ['C'] = PgErrorCodes.UniqueViolation,
            ['M'] = "duplicate key value violates unique constraint",
            ['D'] = "Key (id)=(1) already exists.",
            ['H'] = "Choose another id.",
            ['P'] = "12",
            ['p'] = "7",
            ['q'] = "SELECT 1",
            ['W'] = "PL/pgSQL function f() line 1",
            ['s'] = "public",
            ['t'] = "items",
            ['c'] = "id",
            ['d'] = "integer",
            ['n'] = "items_pkey",
            ['F'] = "nbtinsert.c",
            ['L'] = "666",
            ['R'] = "_bt_check_unique",
        };

        PgException exception = new(fields);

        Assert.AreEqual(fields['M'], exception.Message);
        Assert.AreEqual(fields['V'], exception.Severity);
        Assert.AreEqual(fields['C'], exception.SqlState);
        Assert.AreEqual(fields['D'], exception.Detail);
        Assert.AreEqual(fields['H'], exception.Hint);
        Assert.AreEqual(12, exception.Position);
        Assert.AreEqual(7, exception.InternalPosition);
        Assert.AreEqual(fields['q'], exception.InternalQuery);
        Assert.AreEqual(fields['W'], exception.Where);
        Assert.AreEqual(fields['s'], exception.SchemaName);
        Assert.AreEqual(fields['t'], exception.TableName);
        Assert.AreEqual(fields['c'], exception.ColumnName);
        Assert.AreEqual(fields['d'], exception.DataTypeName);
        Assert.AreEqual(fields['n'], exception.ConstraintName);
        Assert.AreEqual(fields['F'], exception.File);
        Assert.AreEqual(fields['L'], exception.Line);
        Assert.AreEqual(fields['R'], exception.Routine);
    }

    [TestMethod]
    public void PreservesFallbacksAndIgnoresInvalidPositions()
    {
        PgException exception = new(new Dictionary<char, string>
        {
            ['S'] = "ERROR",
            ['P'] = "not a number",
            ['p'] = "not a number",
        });

        Assert.AreEqual("PostgreSQL error", exception.Message);
        Assert.AreEqual("ERROR", exception.Severity);
        Assert.IsNull(exception.Position);
        Assert.IsNull(exception.InternalPosition);
    }

    [TestMethod]
    [DataRow(PgErrorCodes.SerializationFailure)]
    [DataRow(PgErrorCodes.QueryCanceled)]
    [DataRow(PgErrorCodes.ConnectionFailure)]
    [DataRow(PgErrorCodes.AdminShutdown)]
    [DataRow(PgErrorCodes.CannotConnectNow)]
    [DataRow(PgErrorCodes.DeadlockDetected)]
    public void RecognizesTransientErrors(string sqlState)
    {
        Assert.IsTrue(Create(sqlState).IsTransient);
    }

    [TestMethod]
    [DataRow(PgErrorCodes.UniqueViolation)]
    [DataRow(PgErrorCodes.UndefinedTable)]
    [DataRow(PgErrorCodes.UndefinedColumn)]
    [DataRow(PgErrorCodes.InFailedSqlTransaction)]
    public void DoesNotClassifyOrdinaryErrorsAsTransient(string sqlState)
    {
        Assert.IsFalse(Create(sqlState).IsTransient);
    }

    [TestMethod]
    [DataRow(PgErrorCodes.ConnectionException)]
    [DataRow(PgErrorCodes.ConnectionFailure)]
    [DataRow(PgErrorCodes.AdminShutdown)]
    [DataRow(PgErrorCodes.CannotConnectNow)]
    public void RecognizesFatalErrors(string sqlState)
    {
        Assert.IsTrue(Create(sqlState).IsFatal);
    }

    [TestMethod]
    [DataRow(PgErrorCodes.InFailedSqlTransaction)]
    [DataRow(PgErrorCodes.SerializationFailure)]
    [DataRow(PgErrorCodes.DeadlockDetected)]
    public void RecognizesTransactionAbortErrors(string sqlState)
    {
        Assert.IsTrue(Create(sqlState).IsTransactionAbort);
    }

    [TestMethod]
    public void DoesNotClassifyUnknownOrMissingSqlState()
    {
        PgException unknown = Create("ZZZZZ");
        PgException missing = new(new Dictionary<char, string>());

        Assert.IsFalse(unknown.IsTransient);
        Assert.IsFalse(unknown.IsFatal);
        Assert.IsFalse(unknown.IsTransactionAbort);
        Assert.IsFalse(missing.IsTransient);
        Assert.IsFalse(missing.IsFatal);
        Assert.IsFalse(missing.IsTransactionAbort);
    }

    private static PgException Create(string sqlState) =>
      new(new Dictionary<char, string> { ['C'] = sqlState });
}
