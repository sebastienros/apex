using System.Data.Common;
using Apex.SqlClient;

namespace Apex.PgClient;

public sealed class PgDbBatch : ApexDbBatch
{
    public PgDbBatch() : base(new PgDbBatchCommandCollection()) { }
    protected override ApexDbBatchCommand CreateBatchCommandCore() => new PgDbBatchCommand();
    protected override ApexDbCommand CreateCommandCore(ApexDbBatchCommand command, DbConnection connection)
    {
        var result = new PgDbCommand((PgDbConnection)connection) { CommandText = command.CommandText };
        foreach (DbParameter parameter in command.Parameters) result.Parameters.Add(parameter);
        return result;
    }
    protected override void ValidateProviderConnection(DbConnection connection)
    {
        if (connection is not PgDbConnection)
        {
            throw new ArgumentException("The batch connection must be a PgDbConnection.", nameof(connection));
        }
    }
    protected override void ValidateProviderTransaction(DbTransaction transaction)
    {
        if (transaction is not PgDbTransaction)
        {
            throw new ArgumentException("The batch transaction must be a PgDbTransaction.", nameof(transaction));
        }
    }
}
public sealed class PgDbBatchCommand : ApexDbBatchCommand
{
    public PgDbBatchCommand() : base(new PgDbParameterCollection()) { }
    protected override ApexDbParameter CreateParameterCore() => new PgDbParameter();
}
public sealed class PgDbBatchCommandCollection : ApexDbBatchCommandCollection { }
