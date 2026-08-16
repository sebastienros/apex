using System.Text;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsCollation(uint Info, byte SortId, int CodePage)
{
    internal const uint Utf8Flag = 0x0400_0000;

    internal int Lcid => checked((int)(Info & 0x000F_FFFF));

    internal bool IsUtf8 => (Info & Utf8Flag) != 0;
}

internal static class TdsCollationCodec
{
    private static readonly IReadOnlyDictionary<int, Encoding> s_encodings = CreateEncodings();

    internal static TdsCollation Read(ref TdsPayloadReader reader, bool unicode)
    {
        var info = reader.ReadUInt32LittleEndian();
        var sortId = reader.ReadByte();
        var codePage = unicode ? 1200 : ResolveCodePage(info, sortId);
        return new TdsCollation(info, sortId, codePage);
    }

    internal static int ResolveCodePage(uint info, byte sortId)
    {
        if ((info & TdsCollation.Utf8Flag) != 0)
        {
            return 65001;
        }

        return sortId == 0
          ? ResolveLcid(checked((int)(info & 0x000F_FFFF)))
          : ResolveSortId(sortId);
    }

    internal static Encoding GetEncoding(int codePage) =>
      s_encodings.TryGetValue(codePage, out var encoding)
        ? encoding
        : throw new InvalidDataException(
          $"SQL Server collation resolved unsupported code page {codePage}.");

    private static IReadOnlyDictionary<int, Encoding> CreateEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int[] codePages =
        [
          437, 850, 874, 932, 936, 949, 950,
      1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258,
    ];
        Dictionary<int, Encoding> encodings = new(codePages.Length + 2)
        {
            [1200] = new UnicodeEncoding(false, false, true),
            [65001] = new UTF8Encoding(false, true),
        };
        foreach (var codePage in codePages)
        {
            encodings[codePage] = Encoding.GetEncoding(
              codePage,
              EncoderFallback.ExceptionFallback,
              DecoderFallback.ExceptionFallback);
        }

        return encodings;
    }

    private static int ResolveLcid(int lcid) =>
      lcid switch
      {
          0x0401 or 0x0420 or 0x0429 or 0x0480 or 0x048C or
        0x0801 or 0x0C01 or 0x1001 or 0x1401 or 0x1801 or
        0x1C01 or 0x2001 or 0x2401 or 0x2801 or 0x2C01 or
        0x3001 or 0x3401 or 0x3801 or 0x3C01 or 0x4001 => 1256,

          0x0402 or 0x0419 or 0x0422 or 0x0423 or 0x0428 or
        0x042F or 0x043F or 0x0440 or 0x0444 or 0x0450 or
        0x046D or 0x0485 or 0x082C or 0x0843 or 0x0850 or
        0x0C1A or 0x1C1A or 0x201A => 1251,

          0x0403 or 0x0406 or 0x0407 or 0x0409 or 0x040A or
        0x040B or 0x040C or 0x040F or 0x0410 or 0x0413 or
        0x0414 or 0x0416 or 0x0417 or 0x041D or 0x0421 or
        0x042B or 0x042D or 0x042E or 0x0432 or 0x0434 or
        0x0435 or 0x0436 or 0x0437 or 0x0438 or 0x043B or
        0x043E or 0x0441 or 0x0452 or 0x0456 or 0x045D or
        0x045E or 0x0462 or 0x0464 or 0x0468 or 0x046A or
        0x046B or 0x046C or 0x046E or 0x046F or 0x0470 or
        0x0478 or 0x047A or 0x047C or 0x047E or 0x0482 or
        0x0483 or 0x0484 or 0x0486 or 0x0487 or 0x0488 or
        0x0807 or 0x0809 or 0x080A or 0x080C or 0x0810 or
        0x0813 or 0x0814 or 0x0816 or 0x081D or 0x082E or
        0x083B or 0x083C or 0x083E or 0x085D or 0x085F or
        0x086B or 0x0C07 or 0x0C09 or 0x0C0A or 0x0C0C or
        0x0C3B or 0x0C6B or 0x1007 or 0x1009 or 0x100A or
        0x100C or 0x103B or 0x1407 or 0x1409 or 0x140A or
        0x140C or 0x143B or 0x1809 or 0x180A or 0x180C or
        0x183B or 0x1C09 or 0x1C0A or 0x1C3B or 0x2009 or
        0x200A or 0x203B or 0x2409 or 0x240A or 0x243B or
        0x2809 or 0x280A or 0x2C09 or 0x2C0A or 0x3009 or
        0x300A or 0x3409 or 0x340A or 0x380A or 0x3C0A or
        0x4009 or 0x400A or 0x4409 or 0x440A or 0x4809 or
        0x480A or 0x4C0A or 0x500A or 0x540A => 1252,

          0x0404 or 0x0C04 or 0x1404 => 950,
          0x0405 or 0x040E or 0x0415 or 0x0418 or 0x041A or
        0x041B or 0x041C or 0x0424 or 0x0442 or 0x081A or
        0x101A or 0x141A or 0x181A => 1250,
          0x0408 => 1253,
          0x040D => 1255,
          0x0411 => 932,
          0x0412 => 949,
          0x041E => 874,
          0x041F or 0x042C or 0x0443 => 1254,
          0x0425 or 0x0426 or 0x0427 or 0x0827 => 1257,
          0x042A => 1258,
          0x0439 or 0x043A or 0x0445 or 0x0446 or 0x0447 or
        0x0448 or 0x0449 or 0x044A or 0x044B or 0x044C or
        0x044D or 0x044E or 0x044F or 0x0451 or 0x0453 or
        0x0454 or 0x0457 or 0x045A or 0x045B or 0x0461 or
        0x0463 or 0x0465 or 0x0481 or 0x0845 => 1200,
          0x0804 or 0x1004 => 936,
          _ => throw new InvalidDataException(
          $"SQL Server collation has unknown LCID 0x{lcid:X5}."),
      };

    private static int ResolveSortId(byte sortId) =>
      sortId switch
      {
          >= 30 and <= 35 => 437,
          >= 40 and <= 45 or 49 or >= 55 and <= 61 => 850,
          >= 50 and <= 54 or >= 71 and <= 75 or >= 183 and <= 186 or
        >= 210 and <= 217 => 1252,
          >= 80 and <= 98 => 1250,
          >= 104 and <= 108 => 1251,
          >= 112 and <= 114 or >= 120 and <= 122 or 124 => 1253,
          >= 128 and <= 130 => 1254,
          >= 136 and <= 138 => 1255,
          >= 144 and <= 146 => 1256,
          >= 152 and <= 160 => 1257,
          192 or 193 or 200 => 932,
          194 or 195 or 201 => 949,
          196 or 197 or 202 => 950,
          198 or 199 or 203 => 936,
          >= 204 and <= 206 => 874,
          _ => throw new InvalidDataException(
          $"SQL Server collation has unknown sort ID {sortId}."),
      };
}
