using System.Text;

namespace Apex.Fortunes;

internal static class FortuneResultValidator
{
    private static readonly string[] s_expectedMessages =
    [
        "Additional fortune added at request time.",
        "fortune: No such file or directory",
        "A computer scientist is someone who fixes things that aren't broken.",
        "After enough decimal places, nobody gives a damn.",
        "A bad random number generator: 1, 1, 1, 1, 1, 4.33e+67, 1, 1, 1",
        "A computer program does what you tell it to do, not what you want it to do.",
        "Emacs is a nice operating system, but I prefer UNIX. \u2014 Tom Christaensen",
        "Any program that runs right is obsolete.",
        "A list is only as strong as its weakest link. \u2014 Donald Knuth",
        "Feature: A bug with seniority.",
        "Computers make very fast, very accurate mistakes.",
        "<script>alert(\"This should not be displayed in a browser alert box.\");</script>",
        "\u30d5\u30ec\u30fc\u30e0\u30ef\u30fc\u30af\u306e\u30d9\u30f3\u30c1\u30de\u30fc\u30af",
    ];

    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static void Validate<T>(
        IReadOnlyList<T> fortunes,
        Func<T, int> getId,
        Func<T, string> getMessage,
        string implementation)
    {
        ValidateCore(fortunes, getId, getMessage, implementation);
    }

    public static void ValidateUtf8<T>(
        IReadOnlyList<T> fortunes,
        Func<T, int> getId,
        Func<T, ReadOnlyMemory<byte>> getMessage,
        string implementation)
    {
        string Decode(T fortune)
        {
            try
            {
                return s_strictUtf8.GetString(getMessage(fortune).Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw Failure(
                    implementation,
                    $"fortune ID {getId(fortune)} contains invalid UTF-8.",
                    exception);
            }
        }

        ValidateCore(fortunes, getId, Decode, implementation);
    }

    private static void ValidateCore<T>(
        IReadOnlyList<T> fortunes,
        Func<T, int> getId,
        Func<T, string> getMessage,
        string implementation)
    {
        if (fortunes.Count != s_expectedMessages.Length)
        {
            throw Failure(
                implementation,
                $"expected {s_expectedMessages.Length} results " +
                $"({s_expectedMessages.Length - 1} database rows plus the request-time row), " +
                $"but received {fortunes.Count}.");
        }

        var seen = new bool[s_expectedMessages.Length];
        string? previousMessage = null;

        for (var index = 0; index < fortunes.Count; index++)
        {
            var fortune = fortunes[index];
            var id = getId(fortune);
            if ((uint)id >= (uint)s_expectedMessages.Length)
            {
                throw Failure(
                    implementation,
                    $"result {index} has unexpected fortune ID {id}; " +
                    $"expected IDs 0 through {s_expectedMessages.Length - 1}.");
            }

            if (seen[id])
            {
                throw Failure(implementation, $"fortune ID {id} was returned more than once.");
            }

            seen[id] = true;
            var message = getMessage(fortune);
            if (!string.Equals(message, s_expectedMessages[id], StringComparison.Ordinal))
            {
                throw Failure(
                    implementation,
                    $"fortune ID {id} has message '{message}', " +
                    $"but expected '{s_expectedMessages[id]}'.");
            }

            if (previousMessage is not null &&
                StringComparer.Ordinal.Compare(previousMessage, message) > 0)
            {
                throw Failure(
                    implementation,
                    $"results are not sorted by message at index {index}: " +
                    $"'{previousMessage}' appears before '{message}'.");
            }

            previousMessage = message;
        }

        for (var id = 0; id < seen.Length; id++)
        {
            if (!seen[id])
            {
                throw Failure(implementation, $"expected fortune ID {id} was not returned.");
            }
        }
    }

    private static InvalidOperationException Failure(
        string implementation,
        string detail,
        Exception? innerException = null) =>
        new(
            $"Fortune startup validation failed for {implementation}: {detail}",
            innerException);
}
