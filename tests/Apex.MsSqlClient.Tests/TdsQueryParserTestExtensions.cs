using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

internal static class TdsQueryParserTestExtensions
{
    /// <summary>
    /// Frames <paramref name="payload"/> as one TABULAR_RESULT message and parses it from a
    /// buffered packet reader, the way a connection does.
    /// </summary>
    internal static TdsQueryResponse Parse(
        this TdsQueryParser parser,
        ReadOnlyMemory<byte> payload,
        Action<MsSqlInfo>? infoHandler = null,
        Action<TdsEnvironmentChangeInfo>? environmentHandler = null,
        Action<TdsReturnValue>? returnValueHandler = null,
        int packetSize = 4096)
    {
        using MemoryStream framed = new();
        TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          payload,
          packetSize,
          default).AsTask().GetAwaiter().GetResult();
        framed.Position = 0;
        var reader = TdsPacketReader.CreateBuffered(framed);
        try
        {
            // A MemoryStream completes every read synchronously, so this never blocks.
            Assert.AreEqual(
              TdsMessageType.TabularResult,
              reader.BeginMessageAsync(default).AsTask().GetAwaiter().GetResult());
            TdsStreamingTokenReader tokens = new(reader, default);
            return parser.ParseMessageAsync(
              tokens,
              infoHandler,
              environmentHandler,
              returnValueHandler).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            reader.Release();
        }
    }
}
