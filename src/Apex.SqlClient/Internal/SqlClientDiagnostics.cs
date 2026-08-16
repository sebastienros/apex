using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Apex.SqlClient.Internal;

internal static class SqlClientDiagnostics
{
    public static readonly ActivitySource ActivitySource = new("Apex.SqlClient");
    private static readonly Meter s_meter = new("Apex.SqlClient");
    private static readonly Histogram<double> s_queryDuration =
      s_meter.CreateHistogram<double>("db.client.operation.duration", "s");
    private static readonly Counter<long> s_queryErrors =
      s_meter.CreateCounter<long>("db.client.operation.errors");

    public static Activity? StartQuery(
        string system,
        string database,
        string host,
        int port,
        string operation)
    {
        var activity = ActivitySource.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", system);
        activity?.SetTag("db.namespace", database);
        activity?.SetTag("server.address", host);
        activity?.SetTag("server.port", port);
        activity?.SetTag("db.operation.name", operation);
        return activity;
    }

    public static void RecordQuery(
        TimeSpan elapsed,
        string system,
        string operation,
        Exception? exception)
    {
        TagList tags = default;
        tags.Add("db.system.name", system);
        tags.Add("db.operation.name", operation);
        s_queryDuration.Record(elapsed.TotalSeconds, tags);
        if (exception is not null)
        {
            tags.Add("error.type", exception.GetType().FullName);
            s_queryErrors.Add(1, tags);
        }
    }
}
