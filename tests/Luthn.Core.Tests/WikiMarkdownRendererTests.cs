using Luthn.Core.Classification;
using Luthn.Core.Wiki;

namespace Luthn.Core.Tests;

public sealed class WikiMarkdownRendererTests
{
    [Fact]
    public void RendererOnlyEmitsSafeProjectionFields()
    {
        var renderer = new WikiMarkdownRenderer();
        var projection = new WikiMarkdownProjection(
            "wiki-1",
            "Customer billing pattern",
            "Use redacted invoice summaries for payment workflow analysis.",
            SensitivityLevel.Internal,
            ["billing", "workflow"],
            [new WikiSourceReference("source-1", "source-event", "redacted-summary", "safe-projection-only", "Safe summary only")]);

        var markdown = renderer.Render(projection);

        Assert.Contains("# Customer billing pattern", markdown);
        Assert.Contains("Use redacted invoice summaries", markdown);
        Assert.Contains("- Core tags: billing, workflow", markdown);
        Assert.Contains("source-1", markdown);
        Assert.Contains("source-event, redacted-summary, safe-projection-only", markdown);
        Assert.DoesNotContain("Customer contract and payment details.", markdown);
    }
}
