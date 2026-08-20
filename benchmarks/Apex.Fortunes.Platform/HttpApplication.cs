// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Connections;

namespace Apex.Fortunes.Platform;

public static class HttpApplicationConnectionBuilderExtensions
{
    public static IConnectionBuilder UseHttpApplication<TConnection>(this IConnectionBuilder builder)
        where TConnection : IHttpConnection, new() =>
        builder.Use(_ => new HttpApplication<TConnection>().ExecuteAsync);
}

public sealed class HttpApplication<TConnection>
    where TConnection : IHttpConnection, new()
{
    public Task ExecuteAsync(ConnectionContext connection)
    {
        var httpConnection = new TConnection
        {
            Reader = connection.Transport.Input,
            Writer = connection.Transport.Output,
        };
        return httpConnection.ExecuteAsync();
    }
}
