using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlParametersTests
{
    [TestMethod]
    public void CreateCopiesInputArray()
    {
        SqlValue[] values = [1, "two", SqlValue.Null];

        SqlParameters parameters = SqlParameters.Create(values);
        values[0] = 42;

        Assert.AreEqual(3, parameters.Count);
        Assert.AreEqual(1, parameters[0].Get<int>());
        Assert.AreEqual("two", parameters[1].Get<string>());
        Assert.IsTrue(parameters[2].IsNull);
    }

    [TestMethod]
    public void CreateFromArgumentListsAndSpansCopiesValues()
    {
        Span<SqlValue> values = [1, "two"];

        var fromSpan = SqlParameters.Create(values);
        values[0] = 42;
        var fromArguments = SqlParameters.Create(3, "four", SqlValue.Null);

        Assert.AreEqual(1, fromSpan[0].Get<int>());
        Assert.AreEqual(3, fromArguments.Count);
        Assert.AreEqual("four", fromArguments[1].Get<string>());
        Assert.AreEqual(0, SqlParameters.Create().Count);
        Assert.ThrowsExactly<ArgumentNullException>(
            () => SqlParameters.Create((SqlValue[])null!));
    }

    [TestMethod]
    public void ReadOnlyMemoryValuesRoundTripTheirRangeWithoutExposingTheOwner()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        SqlValue slice = new ReadOnlyMemory<byte>(bytes, 2, 3);

        var memory = slice.GetRequired<ReadOnlyMemory<byte>>();
        Assert.AreEqual(SqlValueKind.ReadOnlyMemory, slice.Kind);
        Assert.IsTrue(memory.Span.SequenceEqual(new byte[] { 3, 4, 5 }));
        Assert.ThrowsExactly<InvalidCastException>(() => slice.Get<byte[]>());
        Assert.IsInstanceOfType<ReadOnlyMemory<byte>>(slice.ToObject());
        Assert.IsTrue(((ReadOnlyMemory<byte>)slice.ToObject()!).Span.SequenceEqual(memory.Span));

        SqlValue empty = ReadOnlyMemory<byte>.Empty;
        Assert.AreEqual(SqlValueKind.ReadOnlyMemory, empty.Kind);
        Assert.IsTrue(empty.GetRequired<ReadOnlyMemory<byte>>().IsEmpty);

        using TestMemoryManager manager = new([9, 8, 7, 6]);
        SqlValue managed = (ReadOnlyMemory<byte>)manager.Memory[1..3];
        Assert.IsTrue(
            managed.GetRequired<ReadOnlyMemory<byte>>().Span.SequenceEqual(new byte[] { 8, 7 }));
        Assert.IsTrue(
            MemoryMarshal.TryGetMemoryManager(
                managed.GetRequired<ReadOnlyMemory<byte>>(),
                out TestMemoryManager? owner));
        Assert.AreSame(manager, owner);

        SqlValue converted = SqlValue.From(new ReadOnlyMemory<byte>(bytes, 1, 2));
        Assert.IsTrue(
            converted.GetRequired<ReadOnlyMemory<byte>>().Span.SequenceEqual(new byte[] { 2, 3 }));
    }

    private sealed class TestMemoryManager(byte[] bytes) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => bytes;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    [TestMethod]
    public void StoresCommonScalarsWithoutObjectInput()
    {
        SqlParameters parameters = SqlParameters.Create(
          true,
          (short)2,
          3,
          4L,
          5.5f,
          6.5d,
          7.5m);

        Assert.AreEqual(SqlValueKind.Boolean, parameters[0].Kind);
        Assert.AreEqual(3, parameters[2].Get<int>());
        Assert.AreEqual(7.5m, parameters[6].Get<decimal>());
    }

    [TestMethod]
    public void StoresSixteenByteScalarsInline()
    {
        Guid guid = Guid.Parse("12345678-1234-5678-9012-123456789abc");
        DateTimeOffset timestamp = new(2026, 8, 16, 9, 30, 0, TimeSpan.FromHours(-7));

        SqlValue guidValue = guid;
        SqlValue timestampValue = timestamp;

        Assert.AreEqual(guid, guidValue.Get<Guid>());
        Assert.AreEqual(guid, guidValue.ToObject());
        Assert.AreEqual(timestamp, timestampValue.Get<DateTimeOffset>());
        Assert.AreEqual(timestamp, timestampValue.ToObject());
    }

    [TestMethod]
    public void UsesCompactValueLayout()
    {
        Assert.AreEqual(32, Unsafe.SizeOf<SqlValue>());
    }

    [TestMethod]
    public void DistinguishesByteAndSignedByteArrays()
    {
        byte[] bytes = [1, 2];
        sbyte[] signedBytes = [-1, 2];

        SqlValue binary = SqlValue.From(bytes);
        SqlValue signed = SqlValue.From(signedBytes);

        Assert.AreEqual(SqlValueKind.Bytes, binary.Kind);
        Assert.AreSame(bytes, binary.Get<byte[]>());
        Assert.AreEqual(SqlValueKind.Object, signed.Kind);
        Assert.AreSame(signedBytes, signed.Get<sbyte[]>());
    }

    [TestMethod]
    public void DefaultValueIsEmpty()
    {
        SqlParameters parameters = default;

        Assert.AreEqual(0, parameters.Count);
        Assert.AreEqual(0, parameters.Count());
    }

    [TestMethod]
    public void NullReferenceValuesBecomeSqlNull()
    {
        string? text = null;
        byte[]? bytes = null;
        System.Text.Json.JsonDocument? document = null;

        SqlParameters parameters = SqlParameters.Create(text, bytes, document);

        Assert.IsTrue(parameters[0].IsNull);
        Assert.IsTrue(parameters[1].IsNull);
        Assert.IsTrue(parameters[2].IsNull);
        Assert.IsFalse(((SqlValue)"x").IsNull);
    }

    [TestMethod]
    public void ConnectOptionsToStringRedactsThePassword()
    {
        var options = new TestConnectOptions { Host = "db", Username = "app", Password = "s3cret!" };

        var text = options.ToString();

        Assert.DoesNotContain("s3cret!", text);
        Assert.Contains("Password = ***", text);
        Assert.Contains("Host = db", text);
        Assert.Contains("Extra = 7", text);
    }

    private sealed record TestConnectOptions : SqlConnectOptions
    {
        public int Extra { get; init; } = 7;
    }
}
