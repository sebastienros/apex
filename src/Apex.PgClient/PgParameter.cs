using System.Collections;
using Apex.SqlClient;

namespace Apex.PgClient;

public enum PgParameterFormat
{
    Auto,
    Text,
    Binary,
}

/// <summary>A PostgreSQL parameter with explicit server type information.</summary>
public readonly record struct PgParameter
{
    public PgParameter(
        PgType type,
        SqlValue value,
        PgParameterFormat format = PgParameterFormat.Auto)
    {
        if (type.Oid == 0)
        {
            throw new ArgumentException("A PostgreSQL parameter type is required.", nameof(type));
        }

        Type = type;
        Value = value;
        Format = format;
    }

    public PgType Type { get; }

    public SqlValue Value { get; }

    public PgParameterFormat Format { get; }

    public static PgParameter Create(
        PgType type,
        object? value,
        PgParameterFormat format = PgParameterFormat.Auto) =>
        new(type, SqlValue.From(value), format);
}

/// <summary>An immutable ordered set of explicitly typed PostgreSQL parameters.</summary>
public readonly struct PgParameters : IReadOnlyList<PgParameter>
{
    private readonly PgParameter[]? _values;

    private PgParameters(PgParameter[] values)
    {
        _values = values;
    }

    public static PgParameters Empty => default;

    public int Count => _values?.Length ?? 0;

    public PgParameter this[int index] =>
        _values is null
          ? throw new ArgumentOutOfRangeException(nameof(index))
          : _values[index];

    public static PgParameters Create(params PgParameter[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Length == 0
          ? Empty
          : new PgParameters((PgParameter[])values.Clone());
    }

    public static PgParameters From(ReadOnlySpan<PgParameter> values) =>
        values.IsEmpty ? Empty : new PgParameters(values.ToArray());

    public IEnumerator<PgParameter> GetEnumerator() =>
        ((IEnumerable<PgParameter>)(_values ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
