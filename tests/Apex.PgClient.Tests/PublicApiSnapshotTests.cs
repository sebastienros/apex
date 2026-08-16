using Apex.Tests.Shared;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PublicApiSnapshotTests
{
    [TestMethod]
    public void PostgreSqlApiMatchesApprovedSnapshot() =>
      PublicApiSnapshot.Verify(typeof(PgClient).Assembly, "Apex.PgClient.txt");
}
