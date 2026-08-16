namespace Apex.SqlClient;

/// <summary>Describes one result column.</summary>
public sealed record SqlColumn(
    string Name,
    uint TypeId,
    short TypeSize,
    int TypeModifier,
    SqlDataFormat Format);

public enum SqlDataFormat : short
{
    Text = 0,
    Binary = 1,
}
