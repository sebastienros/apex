using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>Thrown when a MySQL column type cannot be represented by the driver.</summary>
public sealed class MySqlUnsupportedTypeException : SqlClientException
{
    /// <summary>Initializes a new instance of the <see cref="MySqlUnsupportedTypeException"/> class.</summary>
    /// <param name="type">The unsupported wire type.</param>
    public MySqlUnsupportedTypeException(MySqlType type)
      : base($"MySQL type 0x{(byte)type:X2} is not supported.")
    {
        Type = type;
    }

    /// <summary>Gets the unsupported wire type.</summary>
    public MySqlType Type { get; }
}
