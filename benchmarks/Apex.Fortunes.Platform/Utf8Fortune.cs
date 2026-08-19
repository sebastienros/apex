namespace Apex.Fortunes.Platform;

public readonly struct Utf8Fortune : IComparable<Utf8Fortune>
{
    public Utf8Fortune(int id, ReadOnlyMemory<byte> message)
    {
        Id = id;
        Message = message;
    }

    public int Id { get; }

    public ReadOnlyMemory<byte> Message { get; }

    public int CompareTo(Utf8Fortune other) =>
        Message.Span.SequenceCompareTo(other.Message.Span);
}
