namespace Apex.SqlClient;

/// <summary>
/// Reads borrowed rows. Field values remain valid only until the next
/// <see cref="ReadAsync"/> call or disposal.
/// </summary>
public interface ISqlRowReader : IAsyncDisposable
{
    IReadOnlyList<SqlColumn> Columns { get; }

    int FieldCount { get; }

    ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default);

    bool IsNull(int ordinal);

    int GetOrdinal(string name);

    /// <summary>
    /// Gets a common CLR or provider-specific value without routing through
    /// object decoding.
    /// </summary>
    T Get<T>(int ordinal);

    T Get<T>(string name) => Get<T>(GetOrdinal(name));

    bool GetBoolean(int ordinal);

    bool GetBoolean(string name) => GetBoolean(GetOrdinal(name));

    short GetInt16(int ordinal);

    short GetInt16(string name) => GetInt16(GetOrdinal(name));

    int GetInt32(int ordinal);

    int GetInt32(string name) => GetInt32(GetOrdinal(name));

    long GetInt64(int ordinal);

    long GetInt64(string name) => GetInt64(GetOrdinal(name));

    float GetFloat(int ordinal);

    float GetFloat(string name) => GetFloat(GetOrdinal(name));

    double GetDouble(int ordinal);

    double GetDouble(string name) => GetDouble(GetOrdinal(name));

    decimal GetDecimal(int ordinal) => Get<decimal>(ordinal);

    decimal GetDecimal(string name) => GetDecimal(GetOrdinal(name));

    string GetString(int ordinal);

    string GetString(string name) => GetString(GetOrdinal(name));

    Guid GetGuid(int ordinal);

    Guid GetGuid(string name) => GetGuid(GetOrdinal(name));

    DateOnly GetDateOnly(int ordinal);

    DateOnly GetDateOnly(string name) => GetDateOnly(GetOrdinal(name));

    TimeOnly GetTimeOnly(int ordinal);

    TimeOnly GetTimeOnly(string name) => GetTimeOnly(GetOrdinal(name));

    DateTime GetDateTime(int ordinal);

    DateTime GetDateTime(string name) => GetDateTime(GetOrdinal(name));

    DateTimeOffset GetDateTimeOffset(int ordinal);

    DateTimeOffset GetDateTimeOffset(string name) =>
      GetDateTimeOffset(GetOrdinal(name));

    byte[] GetBytes(int ordinal);

    byte[] GetBytes(string name) => GetBytes(GetOrdinal(name));

    TElement[]? GetArray<TElement>(int ordinal);

    TElement[]? GetArray<TElement>(string name) =>
      GetArray<TElement>(GetOrdinal(name));
}
