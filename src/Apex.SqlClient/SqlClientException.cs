namespace Apex.SqlClient;

public class SqlClientException : Exception
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
