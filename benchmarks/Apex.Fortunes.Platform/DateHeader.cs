// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Text;
using System.Diagnostics;

namespace Apex.Fortunes.Platform;

internal static class DateHeader
{
    private const int PrefixLength = 8;
    private const int DateTimeRLength = 29;
    private const int SuffixLength = 2;
    private const int SuffixIndex = DateTimeRLength + PrefixLength;

    private static readonly Timer s_timer = new(static _ => SetDateValues(DateTimeOffset.UtcNow), null, 1000, 1000);
    private static byte[] s_headerBytesMaster = new byte[PrefixLength + DateTimeRLength + 2 * SuffixLength];
    private static byte[] s_headerBytesScratch = new byte[PrefixLength + DateTimeRLength + 2 * SuffixLength];

    static DateHeader()
    {
        "\r\nDate: "u8.CopyTo(s_headerBytesMaster);
        "\r\nDate: "u8.CopyTo(s_headerBytesScratch);
        s_headerBytesMaster[SuffixIndex] = (byte)'\r';
        s_headerBytesMaster[SuffixIndex + 1] = (byte)'\n';
        s_headerBytesMaster[SuffixIndex + 2] = (byte)'\r';
        s_headerBytesMaster[SuffixIndex + 3] = (byte)'\n';
        s_headerBytesScratch[SuffixIndex] = (byte)'\r';
        s_headerBytesScratch[SuffixIndex + 1] = (byte)'\n';
        s_headerBytesScratch[SuffixIndex + 2] = (byte)'\r';
        s_headerBytesScratch[SuffixIndex + 3] = (byte)'\n';
        SetDateValues(DateTimeOffset.UtcNow);
        SyncDateTimer();
    }

    public static void SyncDateTimer() => s_timer.Change(1000, 1000);

    public static ReadOnlySpan<byte> HeaderBytes => s_headerBytesMaster;

    private static void SetDateValues(DateTimeOffset value)
    {
        lock (s_headerBytesScratch)
        {
            if (!Utf8Formatter.TryFormat(value, s_headerBytesScratch.AsSpan(PrefixLength), out var written, 'R'))
            {
                throw new Exception("date time format failed");
            }

            Debug.Assert(written == DateTimeRLength);
            (s_headerBytesScratch, s_headerBytesMaster) = (s_headerBytesMaster, s_headerBytesScratch);
        }
    }
}
