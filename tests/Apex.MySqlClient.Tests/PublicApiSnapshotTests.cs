using Apex.Tests.Shared;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class PublicApiSnapshotTests
{
    [TestMethod]
    public void MySqlApiMatchesApprovedSnapshot() =>
      PublicApiSnapshot.Verify(typeof(MySqlClient).Assembly, "Apex.MySqlClient.txt");
}
