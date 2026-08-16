using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlPoolTests
{
    [TestMethod]
    public void DelegatesPoolOptionValidationToSharedCore()
    {
        MySqlConnectOptions connectOptions = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MySqlPool.Create(connectOptions, new SqlPoolOptions { MaximumSize = 0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MySqlPool.Create(connectOptions, new SqlPoolOptions { MaximumWaitQueueSize = -2 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MySqlPool.Create(connectOptions, new SqlPoolOptions { AcquisitionTimeout = TimeSpan.Zero }));
    }

    [TestMethod]
    public void CreateRequiresConnectOptions()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MySqlPool.Create((MySqlConnectOptions)null!));
    }

    [TestMethod]
    public async Task SizeStartsAtZeroBeforeAnyConnectionIsLeased()
    {
        await using MySqlPool pool =
          MySqlPool.Create(new MySqlConnectOptions { Host = "127.0.0.1", Port = 1 });

        Assert.AreEqual(0, pool.Size);
    }
}
