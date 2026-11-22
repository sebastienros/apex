using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.Tests.Shared;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class PublicApiSnapshotTests
{
    [TestMethod]
    public void CommonAdapterApiMatchesApprovedSnapshot() =>
        PublicApiSnapshot.Verify(typeof(ApexDbConnection).Assembly, "Apex.SqlClient.AdoNet.txt");

    [TestMethod]
    public void PostgreSqlAdapterApiMatchesApprovedSnapshot() =>
        PublicApiSnapshot.Verify(typeof(PgDbConnection).Assembly, "Apex.PgClient.AdoNet.txt");

    [TestMethod]
    public void MySqlAdapterApiMatchesApprovedSnapshot() =>
        PublicApiSnapshot.Verify(typeof(MySqlDbConnection).Assembly, "Apex.MySqlClient.AdoNet.txt");

    [TestMethod]
    public void SqlServerAdapterApiMatchesApprovedSnapshot() =>
        PublicApiSnapshot.Verify(typeof(MsSqlDbConnection).Assembly, "Apex.MsSqlClient.AdoNet.txt");
}
