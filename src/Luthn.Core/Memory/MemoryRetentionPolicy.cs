namespace Luthn.Core.Memory;

public sealed class MemoryRetentionPolicy
{
    public MemoryRetentionPolicy(MemoryRetentionKind kind, DateTimeOffset? expiresAt = null)
    {
        if (kind == MemoryRetentionKind.Durable && expiresAt is not null)
        {
            throw new ArgumentException("Durable memory must not have an expiration.", nameof(expiresAt));
        }

        if (kind != MemoryRetentionKind.Durable && expiresAt is null)
        {
            throw new ArgumentException("Non-durable memory must have an expiration.", nameof(expiresAt));
        }

        Kind = kind;
        ExpiresAt = expiresAt;
    }

    public MemoryRetentionKind Kind { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public static MemoryRetentionPolicy Durable() => new(MemoryRetentionKind.Durable);

    public static MemoryRetentionPolicy Session(DateTimeOffset expiresAt) =>
        new(MemoryRetentionKind.Session, expiresAt);

    public static MemoryRetentionPolicy Ephemeral(DateTimeOffset expiresAt) =>
        new(MemoryRetentionKind.Ephemeral, expiresAt);
}
