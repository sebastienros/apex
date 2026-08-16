using Apex.SqlClient;

namespace Apex.PgClient;

public sealed class PgUnsupportedTypeException : SqlClientException
{
    public PgUnsupportedTypeException(uint typeId)
      : base($"PostgreSQL type OID {typeId} is not supported.")
    {
        TypeId = typeId;
    }

    public uint TypeId { get; }
}
