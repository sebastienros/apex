// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.IO.Pipelines;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;

namespace Apex.Fortunes.Platform;

public interface IHttpConnection : IHttpHeadersHandler, IHttpRequestLineHandler
{
    PipeReader Reader { get; set; }

    PipeWriter Writer { get; set; }

    Task ExecuteAsync();
}
