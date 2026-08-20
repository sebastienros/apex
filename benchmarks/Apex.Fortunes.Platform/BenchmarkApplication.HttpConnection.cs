// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;

namespace Apex.Fortunes.Platform;

public sealed partial class BenchmarkApplication : IHttpConnection
{
    private State _state;

    public PipeReader Reader { get; set; } = null!;

    public PipeWriter Writer { get; set; } = null!;

    private HtmlEncoder HtmlEncoder { get; } = CreateHtmlEncoder();

    private HttpParser<ParsingAdapter> Parser { get; } = new();

    public async Task ExecuteAsync()
    {
        try
        {
            await ProcessRequestsAsync();
            Reader.Complete();
        }
        catch (Exception ex)
        {
            Reader.Complete(ex);
        }
        finally
        {
            Writer.Complete();
        }
    }

    private async Task ProcessRequestsAsync()
    {
        while (true)
        {
            var readResult = await Reader.ReadAsync();
            var buffer = readResult.Buffer;
            var isCompleted = readResult.IsCompleted;

            if (buffer.IsEmpty && isCompleted)
            {
                return;
            }

            while (true)
            {
                ParseHttpRequest(ref buffer, isCompleted);

                if (_state == State.Body)
                {
                    await ProcessRequestAsync();
                    _state = State.StartLine;

                    if (!buffer.IsEmpty)
                    {
                        continue;
                    }
                }

                Reader.AdvanceTo(buffer.Start, buffer.End);
                break;
            }

            await Writer.FlushAsync();
        }
    }

    private void ParseHttpRequest(ref ReadOnlySequence<byte> buffer, bool isCompleted)
    {
        var reader = new SequenceReader<byte>(buffer);
        var state = _state;
        if (state == State.StartLine &&
            Parser.ParseRequestLine(new ParsingAdapter(this), ref reader))
        {
            state = State.Headers;
        }

        if (state == State.Headers &&
            Parser.ParseHeaders(new ParsingAdapter(this), ref reader))
        {
            state = State.Body;
        }

        if (state != State.Body && isCompleted)
        {
            throw new InvalidOperationException("Unexpected end of data!");
        }

        _state = state;
        buffer = state == State.Body
            ? buffer.Slice(reader.Position, 0)
            : buffer.Slice(reader.Position);
    }

    private static HtmlEncoder CreateHtmlEncoder()
    {
        var settings = new TextEncoderSettings(
            UnicodeRanges.BasicLatin,
            UnicodeRanges.Katakana,
            UnicodeRanges.Hiragana);
        settings.AllowCharacter('\u2014');
        return HtmlEncoder.Create(settings);
    }

    public void OnStaticIndexedHeader(int index)
    {
    }

    public void OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value)
    {
    }

    public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
    }

    public void OnHeadersComplete(bool endStream)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnChunkedWriter(ChunkedPipeWriter writer) => ChunkedWriterPool.Return(writer);

    private enum State
    {
        StartLine,
        Headers,
        Body,
    }

    private readonly struct ParsingAdapter(BenchmarkApplication requestHandler) :
        IHttpRequestLineHandler,
        IHttpHeadersHandler
    {
        public void OnStaticIndexedHeader(int index) => requestHandler.OnStaticIndexedHeader(index);

        public void OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value) =>
            requestHandler.OnStaticIndexedHeader(index, value);

        public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
            requestHandler.OnHeader(name, value);

        public void OnHeadersComplete(bool endStream) => requestHandler.OnHeadersComplete(endStream);

        public void OnStartLine(
            HttpVersionAndMethod versionAndMethod,
            TargetOffsetPathLength targetPath,
            Span<byte> startLine) =>
            requestHandler.OnStartLine(versionAndMethod, targetPath, startLine);
    }
}
