using System.Text;

namespace Luthn.Core.Wiki;

public sealed class WikiMarkdownRenderer
{
    public string Render(WikiMarkdownProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var markdown = new StringBuilder();
        markdown.Append("# ").AppendLine(EscapeInline(projection.Title));
        markdown.AppendLine();
        markdown.AppendLine("## Summary");
        markdown.AppendLine(EscapeBlock(projection.SafeSummary));
        markdown.AppendLine();
        markdown.AppendLine("## Classification");
        markdown.Append("- Sensitivity: ").AppendLine(EscapeInline(projection.Sensitivity.ToString()));

        if (projection.CoreTags.Count > 0)
        {
            markdown.Append("- Core tags: ")
                .AppendLine(string.Join(", ", projection.CoreTags.Select(EscapeInline)));
        }

        if (projection.SourceReferences.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("## Source References");
            foreach (var reference in projection.SourceReferences)
            {
                markdown.Append("- ")
                    .Append(EscapeInline(reference.SourceId))
                    .Append(" (")
                    .Append(EscapeInline(reference.SourceType))
                    .Append(", ")
                    .Append(EscapeInline(reference.ReferenceKind))
                    .Append(", ")
                    .Append(EscapeInline(reference.RedactionState))
                    .Append("): ")
                    .AppendLine(EscapeInline(reference.RedactionNote));
            }
        }

        return markdown.ToString();
    }

    private static string EscapeInline(string value) =>
        value.Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string EscapeBlock(string value) =>
        EscapeInline(value).Replace("\r\n", "\n", StringComparison.Ordinal);
}
