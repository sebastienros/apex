namespace Apex.Fortunes.Platform;

public readonly struct Fortune : IComparable<Fortune>
{
    public Fortune(int id, string message)
    {
        Id = id;
        Message = message;
    }

    public int Id { get; }

    public string Message { get; }

    public int CompareTo(Fortune other) =>
        StringComparer.Ordinal.Compare(Message, other.Message);
}
