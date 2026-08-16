using Apex.Tests.Shared;

namespace Apex.SqlClient.AzureIdentity.Tests;

[TestClass]
public sealed class PublicApiSnapshotTests
{
    [TestMethod]
    public void AzureIdentityApiMatchesApprovedSnapshot() =>
      PublicApiSnapshot.Verify(
          typeof(AzureIdentityExtensions).Assembly,
          "Apex.SqlClient.AzureIdentity.txt");
}
