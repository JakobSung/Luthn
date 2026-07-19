namespace Luthn.Core.Classification;

/// <summary>
/// Versioned, bounded category taxonomy shared by local and configured
/// classifiers. Category names are stable contract values; marker phrases are
/// detection hints and may grow within the same contract version.
/// </summary>
public static class ClassificationTaxonomy
{
    public const string Version = "1";

    private static readonly CategoryDefinition[] Definitions =
    [
        Restricted("credential", "credential", "credentials", "password", "passcode", "자격 증명", "자격증명", "비밀번호", "암호 번호"),
        Restricted("private key", "private key", "private-key", "개인 키", "개인키", "비밀 키", "비밀키"),
        Restricted("access key", "access key", "access-key", "api key", "api-key", "secret key", "접근 키", "접근키", "액세스 키", "액세스키", "api 키"),
        Restricted("customer original", "customer original", "customer raw", "raw customer", "고객 원문", "고객원문", "고객 원본", "고객원본"),
        Confidential("contract", "contract", "contracts", "계약", "계약서"),
        Confidential("invoice", "invoice", "invoices", "청구서", "송장"),
        Confidential("payment", "payment", "payments", "결제", "지불", "지급 정보", "지급정보"),
        Confidential("tax", "tax", "taxes", "세금", "세무"),
        Confidential("customer", "customer", "customers", "고객"),
        Confidential("email", "email", "e-mail", "email address", "이메일", "전자우편"),
        Confidential("personal identifier", "personal identifier", "personally identifiable information", "pii", "social security number", "passport number", "phone number", "개인정보", "개인 식별 정보", "개인식별정보", "주민등록번호", "주민 번호", "주민번호", "여권번호", "전화번호"),
        Confidential("finance", "finance", "financial record", "financial records", "재무", "금융 정보", "금융정보"),
        Confidential("accounting", "accounting", "accounting record", "accounting records", "회계", "회계 자료", "회계자료"),
        Confidential("private message", "private message", "private messages", "비공개 메시지", "비공개메시지", "사적 메시지", "사적메시지"),
        Confidential("incident log", "incident log", "incident logs", "장애 로그", "장애로그", "사고 로그", "사고로그")
    ];

    public static IReadOnlyList<string> CategoryNames { get; } =
        Definitions.Select(definition => definition.Name).ToArray();

    public static bool IsKnownCategory(string category) =>
        CanonicalNameFor(category) is not null;

    public static string? CanonicalNameFor(string category) =>
        Definitions
            .FirstOrDefault(definition => string.Equals(
                definition.Name,
                category,
                StringComparison.OrdinalIgnoreCase))
            ?.Name;

    public static SensitivityLevel? MinimumSensitivityFor(string category) =>
        Definitions
            .FirstOrDefault(definition => string.Equals(
                definition.Name,
                category,
                StringComparison.OrdinalIgnoreCase))
            ?.MinimumSensitivity;

    public static IReadOnlySet<string> DetectCategories(string content)
    {
        var normalized = content.ToLowerInvariant();
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in Definitions)
        {
            if (definition.Markers.Any(marker => ContainsMarker(normalized, marker)))
            {
                categories.Add(definition.Name);
            }
        }

        return categories;
    }

    private static bool ContainsMarker(string content, string marker)
    {
        var index = 0;
        while ((index = content.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index + marker.Length;
            var startsWithAsciiWord = marker[0] <= sbyte.MaxValue && char.IsLetterOrDigit(marker[0]);
            var endsWithAsciiWord = marker[^1] <= sbyte.MaxValue && char.IsLetterOrDigit(marker[^1]);
            var startsOnBoundary = !startsWithAsciiWord || index == 0 || !char.IsLetterOrDigit(content[index - 1]);
            var endsOnBoundary = !endsWithAsciiWord || end == content.Length || !char.IsLetterOrDigit(content[end]);
            if (startsOnBoundary && endsOnBoundary)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static CategoryDefinition Restricted(string name, params string[] markers) =>
        new(name, SensitivityLevel.Restricted, markers);

    private static CategoryDefinition Confidential(string name, params string[] markers) =>
        new(name, SensitivityLevel.Confidential, markers);

    private sealed record CategoryDefinition(
        string Name,
        SensitivityLevel MinimumSensitivity,
        IReadOnlyList<string> Markers);
}
