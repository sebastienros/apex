using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Apex.SqlClient;

/// <summary>A typed SQL parameter value that avoids boxing common scalar values.</summary>
public readonly struct SqlValue
{
    private readonly SqlValuePayload _payload;
    private readonly object? _reference;

    private SqlValue(
        SqlValueKind kind,
        SqlValuePayload payload = default,
        object? reference = null)
    {
        Kind = kind;
        _payload = payload;
        _reference = reference;
    }

    public static SqlValue Null => default;

    public SqlValueKind Kind { get; }

    public bool IsNull => Kind == SqlValueKind.Null;

    public static SqlValue From(object? value) =>
      value switch
      {
          null => Null,
          SqlValue sqlValue => sqlValue,
          bool typed => typed,
          short typed => typed,
          int typed => typed,
          long typed => typed,
          float typed => typed,
          double typed => typed,
          decimal typed => typed,
          string typed => typed,
          byte[] typed when value.GetType() == typeof(byte[]) => typed,
          sbyte[] typed when value.GetType() == typeof(sbyte[]) =>
          new SqlValue(SqlValueKind.Object, reference: typed),
          ReadOnlyMemory<byte> typed => typed,
          Guid typed => typed,
          DateOnly typed => typed,
          TimeOnly typed => typed,
          DateTime typed => typed,
          DateTimeOffset typed => typed,
          JsonDocument typed => typed,
          JsonElement typed => typed,
          _ => new SqlValue(SqlValueKind.Object, reference: value),
      };

    public T? Get<T>()
    {
        if (IsNull)
        {
            return default;
        }

        if (TryGetKnownValue(out T knownValue))
        {
            return knownValue;
        }

        var value = ToObject();
        if (value is null)
        {
            return default;
        }

        return value is T typed
          ? typed
          : throw new InvalidCastException(
            $"SQL value contains {value.GetType().FullName}, not {typeof(T).FullName}.");
    }

    public T GetRequired<T>()
    {
        if (IsNull)
        {
            throw new InvalidCastException("SQL value contains NULL.");
        }

        if (TryGetKnownValue(out T knownValue))
        {
            return knownValue;
        }

        var value = ToObject();
        if (value is null)
        {
            throw new InvalidCastException("SQL value contains NULL.");
        }

        return value is T typed
          ? typed
          : throw new InvalidCastException(
            $"SQL value contains {value.GetType().FullName}, not {typeof(T).FullName}.");
    }

    public object? ToObject() =>
      Kind switch
      {
          SqlValueKind.Null => null,
          SqlValueKind.Boolean => _payload.Scalar != 0,
          SqlValueKind.Int16 => (short)_payload.Scalar,
          SqlValueKind.Int32 => (int)_payload.Scalar,
          SqlValueKind.Int64 => _payload.Scalar,
          SqlValueKind.Single => BitConverter.Int32BitsToSingle((int)_payload.Scalar),
          SqlValueKind.Double => BitConverter.Int64BitsToDouble(_payload.Scalar),
          SqlValueKind.Decimal => _payload.Decimal,
          SqlValueKind.Guid => _payload.Guid,
          SqlValueKind.DateOnly => DateOnly.FromDayNumber((int)_payload.Scalar),
          SqlValueKind.TimeOnly => new TimeOnly(_payload.Scalar),
          SqlValueKind.DateTime => DateTime.FromBinary(_payload.Scalar),
          SqlValueKind.DateTimeOffset => _payload.DateTimeOffset,
          _ => _reference,
      };

    public static implicit operator SqlValue(bool value) =>
      new(SqlValueKind.Boolean, new SqlValuePayload(value ? 1 : 0));

    public static implicit operator SqlValue(short value) =>
      new(SqlValueKind.Int16, new SqlValuePayload(value));

    public static implicit operator SqlValue(int value) =>
      new(SqlValueKind.Int32, new SqlValuePayload(value));

    public static implicit operator SqlValue(long value) =>
      new(SqlValueKind.Int64, new SqlValuePayload(value));

    public static implicit operator SqlValue(float value) =>
      new(
        SqlValueKind.Single,
        new SqlValuePayload(BitConverter.SingleToInt32Bits(value)));

    public static implicit operator SqlValue(double value) =>
      new(
        SqlValueKind.Double,
        new SqlValuePayload(BitConverter.DoubleToInt64Bits(value)));

    public static implicit operator SqlValue(decimal value) =>
      new(SqlValueKind.Decimal, new SqlValuePayload(value));

    public static implicit operator SqlValue(string value) =>
      new(SqlValueKind.String, reference: value);

    public static implicit operator SqlValue(byte[] value) =>
      new(SqlValueKind.Bytes, reference: value);

    public static implicit operator SqlValue(ReadOnlyMemory<byte> value) =>
      new(SqlValueKind.ReadOnlyMemory, reference: value);

    public static implicit operator SqlValue(Guid value) =>
      new(SqlValueKind.Guid, new SqlValuePayload(value));

    public static implicit operator SqlValue(DateOnly value) =>
      new(SqlValueKind.DateOnly, new SqlValuePayload(value.DayNumber));

    public static implicit operator SqlValue(TimeOnly value) =>
      new(SqlValueKind.TimeOnly, new SqlValuePayload(value.Ticks));

    public static implicit operator SqlValue(DateTime value) =>
      new(SqlValueKind.DateTime, new SqlValuePayload(value.ToBinary()));

    public static implicit operator SqlValue(DateTimeOffset value) =>
      new(SqlValueKind.DateTimeOffset, new SqlValuePayload(value));

    public static implicit operator SqlValue(JsonDocument value) =>
      new(SqlValueKind.JsonDocument, reference: value);

    public static implicit operator SqlValue(JsonElement value) =>
      new(SqlValueKind.JsonElement, reference: value);

    private bool TryGetKnownValue<T>(out T value)
    {
        switch (Kind)
        {
            case SqlValueKind.Boolean when typeof(T) == typeof(bool):
                value = Reinterpret<bool, T>(_payload.Scalar != 0);
                return true;
            case SqlValueKind.Int16 when typeof(T) == typeof(short):
                value = Reinterpret<short, T>((short)_payload.Scalar);
                return true;
            case SqlValueKind.Int32 when typeof(T) == typeof(int):
                value = Reinterpret<int, T>((int)_payload.Scalar);
                return true;
            case SqlValueKind.Int64 when typeof(T) == typeof(long):
                value = Reinterpret<long, T>(_payload.Scalar);
                return true;
            case SqlValueKind.Single when typeof(T) == typeof(float):
                value = Reinterpret<float, T>(
                  BitConverter.Int32BitsToSingle((int)_payload.Scalar));
                return true;
            case SqlValueKind.Double when typeof(T) == typeof(double):
                value = Reinterpret<double, T>(
                  BitConverter.Int64BitsToDouble(_payload.Scalar));
                return true;
            case SqlValueKind.Decimal when typeof(T) == typeof(decimal):
                value = Reinterpret<decimal, T>(_payload.Decimal);
                return true;
            case SqlValueKind.Guid when typeof(T) == typeof(Guid):
                value = Reinterpret<Guid, T>(_payload.Guid);
                return true;
            case SqlValueKind.DateOnly when typeof(T) == typeof(DateOnly):
                value = Reinterpret<DateOnly, T>(
                  DateOnly.FromDayNumber((int)_payload.Scalar));
                return true;
            case SqlValueKind.TimeOnly when typeof(T) == typeof(TimeOnly):
                value = Reinterpret<TimeOnly, T>(new TimeOnly(_payload.Scalar));
                return true;
            case SqlValueKind.DateTime when typeof(T) == typeof(DateTime):
                value = Reinterpret<DateTime, T>(
                  DateTime.FromBinary(_payload.Scalar));
                return true;
            case SqlValueKind.DateTimeOffset when typeof(T) == typeof(DateTimeOffset):
                value = Reinterpret<DateTimeOffset, T>(_payload.DateTimeOffset);
                return true;
            default:
                if (_reference is T reference)
                {
                    value = reference;
                    return true;
                }

                value = default!;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TTo Reinterpret<TFrom, TTo>(TFrom value)
      where TFrom : struct =>
      Unsafe.As<TFrom, TTo>(ref value);

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct SqlValuePayload
    {
        [FieldOffset(0)]
        public readonly long Scalar;

        [FieldOffset(0)]
        public readonly decimal Decimal;

        [FieldOffset(0)]
        public readonly Guid Guid;

        [FieldOffset(0)]
        public readonly DateTimeOffset DateTimeOffset;

        public SqlValuePayload(long value)
        {
            this = default;
            Scalar = value;
        }

        public SqlValuePayload(decimal value)
        {
            this = default;
            Decimal = value;
        }

        public SqlValuePayload(Guid value)
        {
            this = default;
            Guid = value;
        }

        public SqlValuePayload(DateTimeOffset value)
        {
            this = default;
            DateTimeOffset = value;
        }
    }
}

public enum SqlValueKind : byte
{
    Null,
    Boolean,
    Int16,
    Int32,
    Int64,
    Single,
    Double,
    Decimal,
    String,
    Bytes,
    ReadOnlyMemory,
    Guid,
    DateOnly,
    TimeOnly,
    DateTime,
    DateTimeOffset,
    JsonDocument,
    JsonElement,
    Object,
}
