using System.Data.Common;

namespace Apex.SqlClient;

public class SqlClientException : DbException
{
    public SqlClientException(string message)
        : base(message)
    {
    }

    public SqlClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
