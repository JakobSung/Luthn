namespace Luthn.Host.Api;

internal static class SensitiveAccessLifecycleGate
{
    internal static SemaphoreSlim Instance { get; } = new(1, 1);
}
