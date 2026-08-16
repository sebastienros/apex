[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Apex.MsSqlClient.IntegrationTests;

[TestClass]
public sealed class AssemblyLifecycle
{
    [AssemblyInitialize]
    public static Task InitializeAsync(TestContext _) =>
      MsSqlTestEnvironment.StartAsync();

    [AssemblyCleanup]
    public static Task CleanupAsync() =>
      MsSqlTestEnvironment.StopAsync();
}
