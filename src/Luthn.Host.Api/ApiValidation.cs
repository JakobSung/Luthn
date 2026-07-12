using Luthn.Core.Common;
using Microsoft.AspNetCore.Mvc;

namespace Luthn.Host.Api;

internal static class ApiValidation
{
    public const int PublicRecordIdMaxLength = 128;
    public const int SourceTextMaxLength = 128;
    public const int SourceContentMaxLength = 20_000;
    public const int TitleMaxLength = 200;
    public const int SafeSummaryMaxLength = 4000;
    public const int ReasonMaxLength = 1000;
    public const int CoreTagMaxLength = 64;
    public const int CoreTagMaxCount = 32;
    public const int SearchQueryMaxLength = 500;
    public const long RequestBodyMaxBytes = 256 * 1024;

    public static ProblemDetails? ValidateRequiredText(
        string? value,
        string fieldName,
        int maxLength,
        string title)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CreateProblem(title, $"{fieldName} is required.");
        }

        if (value.Trim().Length > maxLength)
        {
            return CreateProblem(title, $"{fieldName} must be {maxLength} characters or fewer.");
        }

        return null;
    }

    public static ProblemDetails? ValidatePublicRecordId(
        string? value,
        string fieldName,
        string title,
        out PublicRecordId? id)
    {
        id = null;
        var textError = ValidateRequiredText(value, fieldName, PublicRecordIdMaxLength, title);
        if (textError is not null)
        {
            return textError;
        }

        try
        {
            id = new PublicRecordId(value!.Trim());
            return null;
        }
        catch (ArgumentException exception)
        {
            return CreateProblem(title, exception.Message);
        }
    }

    public static ProblemDetails? ValidateCoreTags(
        IReadOnlyList<string>? coreTags,
        string fieldName,
        string title,
        bool required)
    {
        if (coreTags is null)
        {
            return required ? CreateProblem(title, $"{fieldName} is required.") : null;
        }

        if (coreTags.Count > CoreTagMaxCount)
        {
            return CreateProblem(title, $"{fieldName} must include {CoreTagMaxCount} tags or fewer.");
        }

        foreach (var tag in coreTags)
        {
            if (tag is not null && tag.Trim().Length > CoreTagMaxLength)
            {
                return CreateProblem(title, $"{fieldName} entries must be {CoreTagMaxLength} characters or fewer.");
            }
        }

        return null;
    }

    public static ProblemDetails? ValidateOptionalSearchQuery(
        string? query,
        string title)
    {
        if (!string.IsNullOrWhiteSpace(query) &&
            query.Trim().Length > SearchQueryMaxLength)
        {
            return CreateProblem(title, $"query must be {SearchQueryMaxLength} characters or fewer.");
        }

        return null;
    }

    public static ProblemDetails CreateProblem(string title, string detail) =>
        new()
        {
            Title = title,
            Detail = detail
        };
}
