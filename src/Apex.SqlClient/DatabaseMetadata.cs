namespace Apex.SqlClient;

public sealed record DatabaseMetadata(
    string ProductName,
    string FullVersion,
    int MajorVersion,
    int MinorVersion);
