using System.Data.Common;
using Apex.SqlClient;

namespace Apex.MsSqlClient;

public sealed class MsSqlDbBatch : ApexDbBatch
{
    public MsSqlDbBatch() : base(new MsSqlDbBatchCommandCollection()) { }
    protected override ApexDbBatchCommand CreateBatchCommandCore() => new MsSqlDbBatchCommand();
    protected override ApexDbCommand CreateCommandCore(ApexDbBatchCommand command, DbConnection connection)
    {
        var result = new MsSqlDbCommand((MsSqlDbConnection)connection) { CommandText = command.CommandText };
        foreach (DbParameter parameter in command.Parameters) result.Parameters.Add(parameter);
        return result;
    }
    protected override void ValidateProviderConnection(DbConnection connection)
    {
        if (connection is not MsSqlDbConnection)
        {
            throw new ArgumentException("The batch connection must be a MsSqlDbConnection.", nameof(connection));
        }
    }
    protected override void ValidateProviderTransaction(DbTransaction transaction)
    {
        if (transaction is not MsSqlDbTransaction)
        {
            throw new ArgumentException("The batch transaction must be a MsSqlDbTransaction.", nameof(transaction));
        }
    }
}
public sealed class MsSqlDbBatchCommand : ApexDbBatchCommand
{
    public MsSqlDbBatchCommand() : base(new MsSqlDbParameterCollection()) { }
    protected override ApexDbParameter CreateParameterCore() => new MsSqlDbParameter();
}
public sealed class MsSqlDbBatchCommandCollection : ApexDbBatchCommandCollection { }
