using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class AsyncAutoResetEventTests
{
    [TestMethod]
    public async Task CompletesWaitingConsumer()
    {
        AsyncAutoResetEvent signal = new();
        var waiting = signal.WaitAsync();

        Assert.IsFalse(waiting.IsCompleted);
        signal.Set();
        await waiting;
    }

    [TestMethod]
    public void RemembersOneSignal()
    {
        AsyncAutoResetEvent signal = new();

        signal.Set();

        Assert.IsTrue(signal.WaitAsync().IsCompletedSuccessfully);
        Assert.IsFalse(signal.WaitAsync().IsCompleted);
    }

    [TestMethod]
    public void RejectsConcurrentWaiters()
    {
        AsyncAutoResetEvent signal = new();
        _ = signal.WaitAsync();

        Assert.ThrowsExactly<InvalidOperationException>(() => signal.WaitAsync());
    }
}
