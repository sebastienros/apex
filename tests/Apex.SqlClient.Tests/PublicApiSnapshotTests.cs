using Apex.Tests.Shared;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class PublicApiSnapshotTests
{
    [TestMethod]
    public void SharedApiMatchesApprovedSnapshot() =>
      PublicApiSnapshot.Verify(typeof(ISqlClient).Assembly, "Apex.SqlClient.txt");
}
