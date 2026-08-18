using System.Data.Common;
using Apex.SqlClient;

namespace Apex.MySqlClient;

public sealed class MySqlDbBatch : ApexDbBatch
{
    public MySqlDbBatch() : base(new MySqlDbBatchCommandCollection()) { }
    protected override ApexDbBatchCommand CreateBatchCommandCore() => new MySqlDbBatchCommand();
    protected override ApexDbCommand CreateCommandCore(ApexDbBatchCommand command, DbConnection connection)
    {
        var result = new MySqlDbCommand((MySqlDbConnection)connection) { CommandText = command.CommandText };
        foreach (DbParameter parameter in command.Parameters) result.Parameters.Add(parameter);
        return result;
    }
    protected override void ValidateProviderConnection(DbConnection connection)
    {
        if (connection is not MySqlDbConnection)
        {
            throw new ArgumentException("The batch connection must be a MySqlDbConnection.", nameof(connection));
        }
    }
    protected override void ValidateProviderTransaction(DbTransaction transaction)
    {
        if (transaction is not MySqlDbTransaction)
        {
            throw new ArgumentException("The batch transaction must be a MySqlDbTransaction.", nameof(transaction));
        }
    }
}
public sealed class MySqlDbBatchCommand : ApexDbBatchCommand
{
    public MySqlDbBatchCommand() : base(new MySqlDbParameterCollection()) { }
    protected override ApexDbParameter CreateParameterCore() => new MySqlDbParameter();
}
public sealed class MySqlDbBatchCommandCollection : ApexDbBatchCommandCollection { }
