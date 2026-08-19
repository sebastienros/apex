using System.Net;
using System.Runtime.InteropServices;
using Apex.Fortunes.Platform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
var url = new Uri(configuration["urls"] ?? "http://0.0.0.0:5000");

await using var database = await FortuneDatabase.CreateAsync(
    configuration["DATABASE"],
    configuration["DRIVER"],
    configuration["CONNECTION_STRING"]);
await database.ValidateAsync(CancellationToken.None);
BenchmarkApplication.Database = database;
DateHeader.SyncDateTimer();

var hostBuilder = Host.CreateDefaultBuilder()
    .ConfigureWebHost(webHost =>
    {
        webHost
        .UseConfiguration(configuration)
        .UseKestrel(options =>
        {
            options.Listen(IPAddress.Any, url.Port, listen =>
            {
                listen.UseHttpApplication<BenchmarkApplication>();
            });
        })
        .Configure(_ => { })
        .UseSockets(options =>
        {
            options.WaitForDataBeforeAllocatingBuffer = false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                options.UnsafePreferInlineScheduling =
                    Environment.GetEnvironmentVariable(
                        "DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") == "1";
            }
        });
});

using var host = hostBuilder.Build();
await host.StartAsync();
Console.WriteLine("Application started.");
await host.WaitForShutdownAsync();
