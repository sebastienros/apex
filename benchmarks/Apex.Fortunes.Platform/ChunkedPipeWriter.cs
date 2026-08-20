using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Apex.Fortunes.Platform;

internal sealed class ChunkedPipeWriter : PipeWriter
{
    private const int DefaultChunkSizeHint = 2048;
    private static readonly StandardFormat DefaultHexFormat = GetHexFormat(DefaultChunkSizeHint);
    private static ReadOnlySpan<byte> ChunkTerminator => "\r\n"u8;

    private PipeWriter _output = null!;
    private int _chunkSizeHint;
    private StandardFormat _hexFormat = DefaultHexFormat;
    private Memory<byte> _currentFullChunk;
    private Memory<byte> _currentChunk;
    private int _buffered;
    private long _unflushedBytes;
    private bool _ended;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetOutput(PipeWriter output, int chunkSizeHint = DefaultChunkSizeHint)
    {
        _buffered = 0;
        _unflushedBytes = 0;
        _chunkSizeHint = chunkSizeHint;
        _output = output;
        StartNewChunk(chunkSizeHint, isFirst: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        _buffered = 0;
        _unflushedBytes = 0;
        _output = null!;
        _ended = false;
        _hexFormat = DefaultHexFormat;
        _currentFullChunk = default;
        _currentChunk = default;
    }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes => _unflushedBytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Advance(int count)
    {
        ThrowIfEnded();
        _buffered += count;
        _unflushedBytes += count;
        _currentChunk = _currentChunk[count..];
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        ThrowIfEnded();
        if (_currentChunk.Length <= sizeHint)
        {
            EnsureMore(sizeHint);
        }

        return _currentChunk;
    }

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override void CancelPendingFlush() => _output.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
    {
        ThrowIfEnded();
        CommitCurrentChunk(isFinal: true);
        _ended = true;
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        CommitCurrentChunk(isFinal: false);
        var flushTask = _output.FlushAsync(cancellationToken);
        _unflushedBytes = 0;
        return flushTask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static StandardFormat GetHexFormat(int maxValue)
    {
        var hexDigitCount = CountHexDigits(maxValue);
        return new StandardFormat('X', (byte)hexDigitCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountHexDigits(int n) =>
        n <= 16 ? 1 : (BitOperations.Log2((uint)n) >> 2) + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartNewChunk(int sizeHint, bool isFirst = false)
    {
        ThrowIfEnded();

        var oldFullChunkHexLength = -1;
        if (!isFirst)
        {
            oldFullChunkHexLength = CountHexDigits(_currentFullChunk.Length);
        }

        _currentFullChunk = _output.GetMemory(Math.Max(_chunkSizeHint, sizeHint));
        var newFullChunkHexLength = CountHexDigits(_currentFullChunk.Length);
        var currentFullChunkSpan = _currentFullChunk.Span;
        currentFullChunkSpan[..newFullChunkHexLength].Fill((byte)'0');
        "\r\n"u8.CopyTo(currentFullChunkSpan[newFullChunkHexLength..]);
        var chunkHeaderLength = newFullChunkHexLength + 2;
        _currentChunk = _currentFullChunk[chunkHeaderLength..];

        if ((!isFirst && oldFullChunkHexLength != newFullChunkHexLength) ||
            (isFirst && DefaultChunkSizeHint != _chunkSizeHint))
        {
            _hexFormat = GetHexFormat(_currentFullChunk.Length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitCurrentChunk(bool isFinal = false, int sizeHint = 0)
    {
        ThrowIfEnded();

        var contentLength = _buffered;
        if (contentLength <= 0)
        {
            if (isFinal)
            {
                var terminator = "0\r\n\r\n"u8;
                terminator.CopyTo(_currentFullChunk.Span);
                _output.Advance(terminator.Length);
            }

            return;
        }

        var chunkLengthHexDigitsLength = CountHexDigits(contentLength);
        var span = _currentFullChunk.Span;
        if (!Utf8Formatter.TryFormat(contentLength, span, out var bytesWritten, _hexFormat))
        {
            throw new NotSupportedException("Chunk size too large");
        }

        Debug.Assert(chunkLengthHexDigitsLength == bytesWritten, "HEX formatting math problem.");
        var spanOffset = chunkLengthHexDigitsLength + 2 + contentLength;
        var chunkTotalLength = spanOffset + ChunkTerminator.Length;
        Debug.Assert(span.Length >= chunkTotalLength, "Bad chunk size calculation.");
        ChunkTerminator.CopyTo(span[spanOffset..]);

        if (!isFinal)
        {
            _output.Advance(chunkTotalLength);
            StartNewChunk(sizeHint);
        }
        else
        {
            var terminator = "0\r\n\r\n"u8;
            if (chunkTotalLength + terminator.Length <= span.Length)
            {
                terminator.CopyTo(span[chunkTotalLength..]);
                _output.Advance(chunkTotalLength + terminator.Length);
            }
            else
            {
                _output.Advance(chunkTotalLength);
                _output.Write(terminator);
            }
        }

        _buffered = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> source)
    {
        ThrowIfEnded();
        if (_currentChunk.Length >= source.Length + ChunkTerminator.Length)
        {
            source.CopyTo(_currentChunk.Span);
            Advance(source.Length);
        }
        else
        {
            WriteMultiBuffer(source);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnsureMore(int count = 0)
    {
        if (count > _currentChunk.Length - _buffered - ChunkTerminator.Length)
        {
            if (_buffered > 0)
            {
                CommitCurrentChunk(isFinal: false, count);
            }
            else
            {
                StartNewChunk(count);
            }
        }
    }

    private void WriteMultiBuffer(ReadOnlySpan<byte> source)
    {
        while (source.Length > 0)
        {
            if (_currentChunk.Length - ChunkTerminator.Length == 0)
            {
                EnsureMore();
            }

            var writable = Math.Min(source.Length, _currentChunk.Length - ChunkTerminator.Length);
            source[..writable].CopyTo(_currentChunk.Span);
            source = source[writable..];
            Advance(writable);
        }
    }

    private void ThrowIfEnded()
    {
        if (_ended)
        {
            throw new InvalidOperationException("Cannot use the writer after calling End().");
        }
    }
}
