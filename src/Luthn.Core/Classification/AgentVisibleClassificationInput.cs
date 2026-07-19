using System.Text;

namespace Luthn.Core.Classification;

/// <summary>
/// Builds the complete bounded projection that can later be returned through
/// wiki, memory, search, MCP, or agent-context surfaces.
/// </summary>
public static class AgentVisibleClassificationInput
{
    public static string Compose(
        string? content,
        string? title,
        string? safeSummary,
        IEnumerable<string>? coreTags,
        string? projectKey = null,
        string? taskKey = null,
        IEnumerable<string>? topicTags = null)
    {
        var builder = new StringBuilder();
        AppendField(builder, "content", content);
        AppendField(builder, "title", title);
        AppendField(builder, "safeSummary", safeSummary);

        if (coreTags is not null)
        {
            foreach (var tag in coreTags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
            {
                AppendField(builder, "coreTag", tag);
            }
        }

        AppendField(builder, "projectKey", projectKey);
        AppendField(builder, "taskKey", taskKey);
        if (topicTags is not null)
        {
            foreach (var tag in topicTags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
            {
                AppendField(builder, "topicTag", tag);
            }
        }

        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(fieldName);
        builder.Append(':');
        builder.AppendLine();
        builder.Append(value.Trim());
    }
}
