using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Apex.SqlClient.Internal;

internal interface ISqlRowDecoder
{
    int GetFieldCount(ReadOnlyMemory<byte> row);

    bool IsNull(ReadOnlyMemory<byte> row, int ordinal);

    object? DecodeObject(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    T Decode<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        bool copyReadOnlyMemory);

    bool DecodeBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    bool? DecodeNullableBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    short DecodeInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    short? DecodeNullableInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    int DecodeInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    int? DecodeNullableInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    long DecodeInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    long? DecodeNullableInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    float DecodeFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    float? DecodeNullableFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    double DecodeDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    double? DecodeNullableDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    decimal DecodeDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    decimal? DecodeNullableDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    string? DecodeString(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    byte[]? DecodeBytes(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    ReadOnlyMemory<byte> DecodeReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    ReadOnlyMemory<byte>? DecodeNullableReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    Guid DecodeGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    Guid? DecodeNullableGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    DateOnly DecodeDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    DateOnly? DecodeNullableDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    TimeOnly DecodeTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    TimeOnly? DecodeNullableTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    DateTime DecodeDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    DateTime? DecodeNullableDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    DateTimeOffset DecodeDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    DateTimeOffset? DecodeNullableDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    JsonElement DecodeJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    JsonElement? DecodeNullableJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);

    TElement[]? DecodeArray<TElement>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column);
}

internal static class SqlRowDecoder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Decode<T>(
        ISqlRowDecoder decoder,
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        bool copyReadOnlyMemory)
    {
        if (typeof(T) == typeof(string))
        {
            var value = decoder.DecodeString(row, ordinal, column);
            return Unsafe.As<string?, T>(ref value);
        }

        if (typeof(T) == typeof(object))
        {
            return (T)decoder.DecodeObject(row, ordinal, column)!;
        }

        if (typeof(T) == typeof(byte[]))
        {
            var value = decoder.DecodeBytes(row, ordinal, column);
            return Unsafe.As<byte[]?, T>(ref value);
        }

        return decoder.Decode<T>(
          row,
          ordinal,
          column,
          copyReadOnlyMemory);
    }
}
