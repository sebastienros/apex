using System.Text;
using Apex.PgClient.Internal;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgFeatureTests
{
    [TestMethod]
    public void CreatesExplicitTypedParameters()
    {
        PgParameter parameter = PgParameter.Create(
            PgType.Jsonb,
            """{"name":"Apex"}""");
        PgParameters parameters = PgParameters.Create(parameter);

        Assert.AreEqual(PgType.Jsonb, parameters[0].Type);
        Assert.AreEqual("""{"name":"Apex"}""", parameters[0].Value.Get<string>());
        Assert.AreEqual(PgParameterFormat.Auto, parameters[0].Format);
    }

    [TestMethod]
    public void RegistersCustomBinaryCodec()
    {
        PgTypeRegistry registry = new();
        PgType type = new(42_000, "public.custom_text");
        registry.Register<string>(
            type,
            Encoding.UTF8.GetBytes,
            value => Encoding.UTF8.GetString(value.Span));

        Assert.IsTrue(registry.TryGetType(type.Oid, out var byOid));
        Assert.IsTrue(registry.TryGetType(type.Name, out var byName));
        Assert.AreEqual(type, byOid);
        Assert.AreEqual(type, byName);
    }

    [TestMethod]
    public void RejectsInvalidTypedParameter()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new PgParameter(default, SqlValue.Null));
    }

    [TestMethod]
    public void CustomEncoderRunsOnce()
    {
        var calls = 0;
        PgTypeRegistry registry = new();
        PgType type = new(42_001, "public.counted");
        registry.Register<string>(
            type,
            value =>
            {
                calls++;
                return Encoding.UTF8.GetBytes(value);
            },
            value => Encoding.UTF8.GetString(value.Span));
        PgParameter parameter = PgParameter.Create(type, "value");

        var format = PgParameterEncoder.ResolveFormat(parameter, registry);
        var payload = PgParameterEncoder.Encode(parameter, format, registry);

        Assert.AreEqual(PgParameterFormat.Binary, format);
        Assert.AreEqual("value", Encoding.UTF8.GetString(payload));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void TextArraysSupportNullElements()
    {
        var formatted = PgTextCodec.FormatParameter(
            SqlValue.From(new string?[] { "a", null, "b" }));

        Assert.AreEqual("{\"a\",NULL,\"b\"}", formatted);
    }
}
