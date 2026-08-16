using System.Collections.Concurrent;

namespace Apex.PgClient;

public interface IPgTypeCodec
{
    Type ClrType { get; }

    byte[] EncodeBinary(object value);

    object DecodeBinary(ReadOnlyMemory<byte> value);
}

public sealed class PgTypeRegistry
{
    private readonly ConcurrentDictionary<uint, PgType> _typesByOid = new();
    private readonly ConcurrentDictionary<string, PgType> _typesByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, IPgTypeCodec> _codecs = new();

    public PgTypeRegistry()
    {
        RegisterBuiltIns();
    }

    public void Register<T>(
        PgType type,
        Func<T, byte[]> encodeBinary,
        Func<ReadOnlyMemory<byte>, T> decodeBinary)
    {
        ArgumentNullException.ThrowIfNull(encodeBinary);
        ArgumentNullException.ThrowIfNull(decodeBinary);
        RegisterType(type);
        _codecs[type.Oid] = new DelegateCodec<T>(encodeBinary, decodeBinary);
    }

    public bool TryGetType(string name, out PgType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _typesByName.TryGetValue(name, out type);
    }

    public bool TryGetType(uint oid, out PgType type) =>
        _typesByOid.TryGetValue(oid, out type);

    internal bool TryEncode(uint oid, object value, out byte[] payload)
    {
        if (_codecs.TryGetValue(oid, out var codec))
        {
            if (!codec.ClrType.IsInstanceOfType(value))
            {
                throw new InvalidCastException(
                    $"PostgreSQL type OID {oid} expects {codec.ClrType.FullName}, not {value.GetType().FullName}.");
            }

            payload = codec.EncodeBinary(value);
            return true;
        }

        payload = [];
        return false;
    }

    internal bool TryDecode(uint oid, ReadOnlyMemory<byte> value, out object decoded)
    {
        if (_codecs.TryGetValue(oid, out var codec))
        {
            decoded = codec.DecodeBinary(value);
            return true;
        }

        decoded = null!;
        return false;
    }

    internal bool CanDecode(uint oid, Type clrType) =>
        _codecs.TryGetValue(oid, out var codec) && clrType.IsAssignableFrom(codec.ClrType);

    internal bool CanEncode(uint oid, object value) =>
        _codecs.TryGetValue(oid, out var codec) && codec.ClrType.IsInstanceOfType(value);

    internal void RegisterType(PgType type)
    {
        _typesByOid[type.Oid] = type;
        _typesByName[type.Name] = type;
    }

    private void RegisterBuiltIns()
    {
        PgType[] types =
        [
            PgType.Boolean, PgType.Bytea, PgType.Bigint, PgType.Smallint,
            PgType.Integer, PgType.Text, PgType.Json, PgType.Real,
            PgType.DoublePrecision, PgType.Money, PgType.Varchar, PgType.Date,
            PgType.Time, PgType.Timestamp, PgType.TimestampTz, PgType.Interval,
            PgType.TimeTz, PgType.Numeric, PgType.Uuid, PgType.Jsonb,
            PgType.BooleanArray, PgType.ByteaArray, PgType.SmallintArray,
            PgType.IntegerArray, PgType.TextArray, PgType.BigintArray,
            PgType.RealArray, PgType.DoublePrecisionArray, PgType.VarcharArray,
            PgType.DateArray, PgType.TimeArray, PgType.TimestampArray,
            PgType.TimestampTzArray, PgType.IntervalArray, PgType.TimeTzArray,
            PgType.NumericArray, PgType.UuidArray, PgType.JsonArray,
            PgType.JsonbArray, PgType.IntegerRange, PgType.NumericRange,
            PgType.TimestampRange, PgType.TimestampTzRange, PgType.DateRange,
            PgType.BigintRange, PgType.IntegerMultirange, PgType.NumericMultirange,
            PgType.TimestampMultirange, PgType.TimestampTzMultirange,
            PgType.DateMultirange, PgType.BigintMultirange,
            PgType.IntegerRangeArray, PgType.NumericRangeArray,
            PgType.TimestampRangeArray, PgType.TimestampTzRangeArray,
            PgType.DateRangeArray, PgType.BigintRangeArray,
        ];
        foreach (var type in types)
        {
            RegisterType(type);
        }
    }

    private sealed class DelegateCodec<T>(
        Func<T, byte[]> encode,
        Func<ReadOnlyMemory<byte>, T> decode) : IPgTypeCodec
    {
        public Type ClrType => typeof(T);

        public byte[] EncodeBinary(object value) => encode((T)value);

        public object DecodeBinary(ReadOnlyMemory<byte> value) => decode(value)!;
    }
}
