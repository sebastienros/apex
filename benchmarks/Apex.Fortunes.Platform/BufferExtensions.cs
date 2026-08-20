// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Apex.Fortunes.Platform;

public static class BufferExtensions
{
    private const int MaxULongByteLength = 20;

    [ThreadStatic]
    private static byte[]? s_numericBytesScratch;

    internal static void WriteUtf8String<T>(ref this BufferWriter<T> buffer, string text)
        where T : struct, IBufferWriter<byte>
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        buffer.Ensure(byteCount);
        byteCount = Encoding.UTF8.GetBytes(text.AsSpan(), buffer.Span);
        buffer.Advance(byteCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void WriteNumericMultiWrite<T>(ref this BufferWriter<T> buffer, uint number)
        where T : IBufferWriter<byte>
    {
        const byte AsciiDigitStart = (byte)'0';

        var value = number;
        var position = MaxULongByteLength;
        var byteBuffer = NumericBytesScratch;
        do
        {
            var quotient = value / 10;
            byteBuffer[--position] = (byte)(AsciiDigitStart + (value - quotient * 10));
            value = quotient;
        }
        while (value != 0);

        var length = MaxULongByteLength - position;
        buffer.Write(new ReadOnlySpan<byte>(byteBuffer, position, length));
    }

    private static byte[] NumericBytesScratch => s_numericBytesScratch ?? CreateNumericBytesScratch();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] CreateNumericBytesScratch()
    {
        var bytes = new byte[MaxULongByteLength];
        s_numericBytesScratch = bytes;
        return bytes;
    }
}
