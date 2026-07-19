using Microsoft.AspNetCore.DataProtection;

namespace Luthn.Host.Api.Tests;

internal static class TestSensitiveMemoryProtection
{
    public static ISensitiveMemoryPayloadProtector Create() =>
        new DataProtectionSensitiveMemoryPayloadProtector(
            new EphemeralDataProtectionProvider());

    public static SensitiveMemoryProtectionState ReadyState()
    {
        var state = new SensitiveMemoryProtectionState();
        state.MarkReady(0);
        return state;
    }
}
