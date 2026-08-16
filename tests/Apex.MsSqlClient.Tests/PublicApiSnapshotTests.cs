using Apex.Tests.Shared;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class PublicApiSnapshotTests
{
    [TestMethod]
    public void MsSqlApiMatchesApprovedSnapshot() =>
      PublicApiSnapshot.Verify(typeof(MsSqlClient).Assembly, "Apex.MsSqlClient.txt");
}
