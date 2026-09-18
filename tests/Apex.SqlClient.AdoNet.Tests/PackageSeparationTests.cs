using System.Data.Common;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class PackageSeparationTests
{
    [TestMethod]
    public void NativeAssembliesHaveNoAdoNetDependenciesOrTypes()
    {
        foreach (var assembly in new[]
        {
            typeof(ISqlConnection).Assembly,
            typeof(PgConnection).Assembly,
            typeof(MySqlConnection).Assembly,
            typeof(MsSqlConnection).Assembly,
        })
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                Assert.AreNotEqual("System.Data.Common", reference.Name, assembly.FullName);
                Assert.IsFalse(reference.Name!.EndsWith(".AdoNet", StringComparison.Ordinal), assembly.FullName);
            }

            foreach (var type in assembly.GetExportedTypes())
            {
                Assert.IsFalse(type.Name.Contains("Ado", StringComparison.Ordinal), type.FullName);
                Assert.IsFalse(typeof(DbConnection).IsAssignableFrom(type), type.FullName);
                Assert.IsFalse(typeof(DbCommand).IsAssignableFrom(type), type.FullName);
                Assert.IsFalse(typeof(DbDataReader).IsAssignableFrom(type), type.FullName);
                Assert.IsFalse(typeof(DbException).IsAssignableFrom(type), type.FullName);
            }
        }

        Assert.AreEqual(typeof(Exception), typeof(SqlClientException).BaseType);
    }

    [TestMethod]
    public void AdapterAssembliesReferenceOnlyTheirOwnDriverAndCommonAdapter()
    {
        var common = typeof(ApexDbConnection).Assembly;
        CollectionAssert.AreEquivalent(
            new[] { "Apex.SqlClient" },
            common.GetReferencedAssemblies()
                .Where(name => name.Name!.StartsWith("Apex.", StringComparison.Ordinal))
                .Select(name => name.Name!).ToArray());

        foreach (var (adapter, native) in new[]
        {
            (typeof(PgDbConnection).Assembly, typeof(PgConnection).Assembly),
            (typeof(MySqlDbConnection).Assembly, typeof(MySqlConnection).Assembly),
            (typeof(MsSqlDbConnection).Assembly, typeof(MsSqlConnection).Assembly),
        })
        {
            Assert.AreNotEqual(adapter, native);
            CollectionAssert.AreEquivalent(
                new[] { "Apex.SqlClient", "Apex.SqlClient.AdoNet", native.GetName().Name! },
                adapter.GetReferencedAssemblies()
                    .Where(name => name.Name!.StartsWith("Apex.", StringComparison.Ordinal))
                    .Select(name => name.Name!).ToArray());
        }
    }
}
