namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlAuthenticationCredentialTests
{
    [TestMethod]
    public void ToStringDoesNotExposeSecret()
    {
        SqlAuthenticationCredential credential = new("sensitive-token");

        Assert.IsFalse(credential.ToString()!.Contains("sensitive-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EmptySecretIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new SqlAuthenticationCredential(string.Empty));
    }
}
