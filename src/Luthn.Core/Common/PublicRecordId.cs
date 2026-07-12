namespace Luthn.Core.Common;

public sealed record PublicRecordId
{
    public PublicRecordId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Public record id is required.", nameof(value));
        }

        if (value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Public record id cannot contain whitespace.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
