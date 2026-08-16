namespace Apex.PgClient;

/// <summary>Identifies a PostgreSQL type by its wire-protocol object identifier.</summary>
public readonly record struct PgType
{
    public PgType(uint oid, string name)
    {
        ArgumentOutOfRangeException.ThrowIfZero(oid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Oid = oid;
        Name = name;
    }

    public uint Oid { get; }

    public string Name { get; }

    public static PgType Boolean { get; } = new(16, "boolean");
    public static PgType Bytea { get; } = new(17, "bytea");
    public static PgType Bigint { get; } = new(20, "bigint");
    public static PgType Smallint { get; } = new(21, "smallint");
    public static PgType Integer { get; } = new(23, "integer");
    public static PgType Text { get; } = new(25, "text");
    public static PgType Json { get; } = new(114, "json");
    public static PgType Real { get; } = new(700, "real");
    public static PgType DoublePrecision { get; } = new(701, "double precision");
    public static PgType Money { get; } = new(790, "money");
    public static PgType Varchar { get; } = new(1043, "character varying");
    public static PgType Date { get; } = new(1082, "date");
    public static PgType Time { get; } = new(1083, "time without time zone");
    public static PgType Timestamp { get; } = new(1114, "timestamp without time zone");
    public static PgType TimestampTz { get; } = new(1184, "timestamp with time zone");
    public static PgType Interval { get; } = new(1186, "interval");
    public static PgType TimeTz { get; } = new(1266, "time with time zone");
    public static PgType Numeric { get; } = new(1700, "numeric");
    public static PgType Uuid { get; } = new(2950, "uuid");
    public static PgType Jsonb { get; } = new(3802, "jsonb");
    public static PgType IntegerRange { get; } = new(3904, "int4range");
    public static PgType NumericRange { get; } = new(3906, "numrange");
    public static PgType TimestampRange { get; } = new(3908, "tsrange");
    public static PgType TimestampTzRange { get; } = new(3910, "tstzrange");
    public static PgType DateRange { get; } = new(3912, "daterange");
    public static PgType BigintRange { get; } = new(3926, "int8range");
    public static PgType IntegerMultirange { get; } = new(4451, "int4multirange");
    public static PgType NumericMultirange { get; } = new(4532, "nummultirange");
    public static PgType TimestampMultirange { get; } = new(4533, "tsmultirange");
    public static PgType TimestampTzMultirange { get; } = new(4534, "tstzmultirange");
    public static PgType DateMultirange { get; } = new(4535, "datemultirange");
    public static PgType BigintMultirange { get; } = new(4536, "int8multirange");

    public static PgType BooleanArray { get; } = new(1000, "boolean[]");
    public static PgType ByteaArray { get; } = new(1001, "bytea[]");
    public static PgType SmallintArray { get; } = new(1005, "smallint[]");
    public static PgType IntegerArray { get; } = new(1007, "integer[]");
    public static PgType TextArray { get; } = new(1009, "text[]");
    public static PgType BigintArray { get; } = new(1016, "bigint[]");
    public static PgType RealArray { get; } = new(1021, "real[]");
    public static PgType DoublePrecisionArray { get; } = new(1022, "double precision[]");
    public static PgType VarcharArray { get; } = new(1015, "character varying[]");
    public static PgType DateArray { get; } = new(1182, "date[]");
    public static PgType TimeArray { get; } = new(1183, "time without time zone[]");
    public static PgType TimestampArray { get; } = new(1115, "timestamp without time zone[]");
    public static PgType TimestampTzArray { get; } = new(1185, "timestamp with time zone[]");
    public static PgType IntervalArray { get; } = new(1187, "interval[]");
    public static PgType TimeTzArray { get; } = new(1270, "time with time zone[]");
    public static PgType NumericArray { get; } = new(1231, "numeric[]");
    public static PgType UuidArray { get; } = new(2951, "uuid[]");
    public static PgType JsonArray { get; } = new(199, "json[]");
    public static PgType JsonbArray { get; } = new(3807, "jsonb[]");
    public static PgType IntegerRangeArray { get; } = new(3905, "int4range[]");
    public static PgType NumericRangeArray { get; } = new(3907, "numrange[]");
    public static PgType TimestampRangeArray { get; } = new(3909, "tsrange[]");
    public static PgType TimestampTzRangeArray { get; } = new(3911, "tstzrange[]");
    public static PgType DateRangeArray { get; } = new(3913, "daterange[]");
    public static PgType BigintRangeArray { get; } = new(3927, "int8range[]");

    public override string ToString() => Name;
}
