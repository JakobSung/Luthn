using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Luthn.Core.Common;

namespace Luthn.Core.Classification;

/// <summary>
/// Detects bounded, high-confidence secret and PII shapes without returning or
/// retaining the matched value. The result contains taxonomy categories only.
/// </summary>
public sealed class DeterministicSensitiveDataDetector
{
    public const string Version = "3";
    public const string RedactionMarker = "[redacted]";

    private static readonly Regex PrivateKeyPattern = CreatePattern(
        @"-----BEGIN\s+(?:(?:RSA|EC|DSA|OPENSSH)\s+)?PRIVATE\s+KEY-----",
        ignoreCase: true);
    private static readonly Regex PrivateKeyBlockPattern = CreatePattern(
        @"-----BEGIN\s+(?:(?:RSA|EC|DSA|OPENSSH)\s+)?PRIVATE\s+KEY-----[\s\S]*?-----END\s+(?:(?:RSA|EC|DSA|OPENSSH)\s+)?PRIVATE\s+KEY-----",
        ignoreCase: true);
    private static readonly Regex AccessTokenPattern = CreatePattern(
        @"(?<![A-Za-z0-9])(?:(?:AKIA|ASIA)[A-Z0-9]{16}|gh[pousr]_[A-Za-z0-9]{30,255}|sk-[A-Za-z0-9_-]{20,255})(?![A-Za-z0-9])");
    private static readonly Regex AccessSecretAssignmentPattern = CreatePattern(
        """(?:api[_ -]?key|access[_ -]?token|secret[_ -]?key|bearer(?:\s+token)?|api\s*키|접근\s*키|액세스\s*키)\s*[:=]\s*['"]?[A-Za-z0-9_./+\-=]{12,255}['"]?""",
        ignoreCase: true);
    private static readonly Regex ClientSecretAssignmentPattern = CreatePattern(
        """(?:client[_ -]?(?:secret|token)|oauth[_ -]?secret|private[_ -]?token)\s*[:=]\s*['"]?[^\s;'\"]{8,255}['"]?""",
        ignoreCase: true);
    private static readonly Regex CredentialAssignmentPattern = CreatePattern(
        """(?:password|passcode|비밀번호|암호(?:\s*번호)?)\s*[:=]\s*['"]?[^\s'"]{8,255}['"]?""",
        ignoreCase: true);
    private static readonly Regex JwtPattern = CreatePattern(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])");
    private static readonly Regex ConnectionStringPattern = CreatePattern(
        @"(?:(?:Server|Data\s*Source|DataSource|Host|Endpoint|AccountEndpoint)\s*=\s*[^;\r\n]+;(?:(?:Database|Initial\s+Catalog|User\s*Id|Uid|Username|Password|Pwd|AccountKey|Access\s*Key|Secret)\s*=\s*[^;\r\n]+;?)+|(?:postgres(?:ql)?|mysql|mssql|mongodb(?:\+srv)?|redis)://[^\s""']+)",
        ignoreCase: true);
    // Protected access handles are intentionally bare 64-character lowercase
    // hex values, so the contract shape itself must be treated as restricted.
    private static readonly Regex ProtectedAccessHandlePattern = CreatePattern(
        @"(?<![A-Za-z0-9])[0-9a-f]{64}(?![A-Za-z0-9])");
    private static readonly Regex EmailPattern = CreatePattern(
        @"(?<![A-Za-z0-9.!#$%&'*+/=?^_`{|}~-])[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+(?![A-Za-z0-9-])",
        ignoreCase: true);
    private static readonly Regex KoreanPhonePattern = CreatePattern(
        @"(?<!\d)(?:(?:\+82[- ]?1[016789])|(?:01[016789]))[- ]?\d{3,4}[- ]?\d{4}(?!\d)");
    private static readonly Regex KoreanResidentRegistrationPattern = CreatePattern(
        @"(?<!\d)(?<birth>\d{6})-(?<type>[1-8])\d{6}(?!\d)");
    private static readonly Regex PaymentCardCandidatePattern = CreatePattern(
        @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)");

    public ClassificationResult Detect(PublicRecordId sourceId, string? content) =>
        Detect(sourceId, content, includeMonetaryContext: true);

    private ClassificationResult Detect(
        PublicRecordId sourceId,
        string? content,
        bool includeMonetaryContext)
    {
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(content))
        {
            if (PrivateKeyPattern.IsMatch(content))
            {
                categories.Add("private key");
            }

            if (AccessTokenPattern.IsMatch(content) || AccessSecretAssignmentPattern.IsMatch(content))
            {
                categories.Add("access key");
            }

            if (ClientSecretAssignmentPattern.IsMatch(content) ||
                CredentialAssignmentPattern.IsMatch(content) ||
                JwtPattern.IsMatch(content) ||
                ConnectionStringPattern.IsMatch(content))
            {
                categories.Add("credential");
            }

            if (ProtectedAccessHandlePattern.IsMatch(content))
            {
                categories.Add("access handle");
            }

            if (EmailPattern.IsMatch(content))
            {
                categories.Add("email");
            }

            if (KoreanPhonePattern.IsMatch(content) || ContainsValidKoreanResidentRegistrationNumber(content))
            {
                categories.Add("personal identifier");
            }

            if (ContainsLuhnValidPaymentCard(content))
            {
                categories.Add("payment");
            }

            var monetary = BoundedMonetaryAnalyzer.Analyze(content);
            if (monetary.HasAmountExpression ||
                (includeMonetaryContext && monetary.HasSensitiveExpression))
            {
                categories.Add("finance");
            }
        }

        return ClassificationResultNormalizer.Normalize(new ClassificationResult(
            sourceId,
            SensitivityLevel.Public,
            categories.Count == 0 ? 0 : 1,
            categories,
            categories.Count > 0));
    }

    /// <summary>
    /// Replaces only bounded, high-confidence secret and PII values. The
    /// result never includes the matched values or their offsets. If a shape
    /// cannot be removed completely, callers must keep the whole item behind
    /// the private boundary.
    /// </summary>
    public SensitiveDataRedactionResult Redact(string? content)
    {
        var value = content ?? "";
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SensitiveDataRedactionResult(
                value,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Changed: false,
                IsComplete: true);
        }

        var ranges = new List<SensitiveRange>();
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMatches(PrivateKeyBlockPattern, value, "private key", ranges, categories);
        var hasIncompletePrivateKey = PrivateKeyPattern.Matches(value)
            .Cast<Match>()
            .Any(header => !ranges.Any(range =>
                range.Start <= header.Index &&
                range.End >= header.Index + header.Length));

        AddMatches(AccessTokenPattern, value, "access key", ranges, categories);
        AddMatches(AccessSecretAssignmentPattern, value, "access key", ranges, categories);
        AddMatches(ClientSecretAssignmentPattern, value, "credential", ranges, categories);
        AddMatches(CredentialAssignmentPattern, value, "credential", ranges, categories);
        AddMatches(JwtPattern, value, "credential", ranges, categories);
        AddMatches(ConnectionStringPattern, value, "credential", ranges, categories);
        AddMatches(ProtectedAccessHandlePattern, value, "access handle", ranges, categories);
        AddMatches(EmailPattern, value, "email", ranges, categories);
        AddMatches(KoreanPhonePattern, value, "personal identifier", ranges, categories);
        AddValidatedMatches(
            KoreanResidentRegistrationPattern,
            value,
            IsValidKoreanResidentRegistrationNumber,
            "personal identifier",
            ranges,
            categories);
        AddValidatedMatches(
            PaymentCardCandidatePattern,
            value,
            IsLuhnValidPaymentCard,
            "payment",
            ranges,
            categories);
        var monetary = BoundedMonetaryAnalyzer.Analyze(value);
        if (monetary.HasSensitiveExpression)
        {
            foreach (var range in monetary.SensitiveRanges)
            {
                ranges.Add(new SensitiveRange(range.Start, range.Length));
            }
            categories.Add("finance");
        }

        if (ranges.Count == 0)
        {
            return new SensitiveDataRedactionResult(
                value,
                categories,
                Changed: false,
                IsComplete: !hasIncompletePrivateKey);
        }

        var redacted = ReplaceRanges(value, ranges);
        var residual = Detect(
            new PublicRecordId("redaction-check"),
            redacted,
            includeMonetaryContext: false);
        return new SensitiveDataRedactionResult(
            redacted,
            categories,
            Changed: !string.Equals(value, redacted, StringComparison.Ordinal),
            IsComplete: !hasIncompletePrivateKey && !residual.ContainsSensitiveMaterial);
    }

    private static bool ContainsValidKoreanResidentRegistrationNumber(string content)
    {
        foreach (Match match in KoreanResidentRegistrationPattern.Matches(content))
        {
            if (IsValidKoreanResidentRegistrationNumber(match))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsLuhnValidPaymentCard(string content)
    {
        foreach (Match match in PaymentCardCandidatePattern.Matches(content))
        {
            if (IsLuhnValidPaymentCard(match))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidKoreanResidentRegistrationNumber(Match match)
    {
        var birth = match.Groups["birth"].ValueSpan;
        if (!int.TryParse(birth[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(birth.Slice(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(birth.Slice(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        var type = match.Groups["type"].ValueSpan[0];
        year += type is '1' or '2' or '5' or '6' ? 1900 : 2000;
        return DateOnly.TryParseExact(
            $"{year:D4}-{month:D2}-{day:D2}",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    private static bool IsLuhnValidPaymentCard(Match match)
    {
        Span<char> digits = stackalloc char[19];
        var length = 0;
        foreach (var character in match.ValueSpan)
        {
            if (char.IsAsciiDigit(character))
            {
                digits[length++] = character;
            }
        }

        return length is >= 13 and <= 19 && PassesLuhn(digits[..length]);
    }

    private static void AddMatches(
        Regex pattern,
        string content,
        string category,
        ICollection<SensitiveRange> ranges,
        ISet<string> categories) =>
        AddValidatedMatches(pattern, content, _ => true, category, ranges, categories);

    private static void AddValidatedMatches(
        Regex pattern,
        string content,
        Func<Match, bool> isValid,
        string category,
        ICollection<SensitiveRange> ranges,
        ISet<string> categories)
    {
        foreach (Match match in pattern.Matches(content))
        {
            if (!isValid(match))
            {
                continue;
            }

            ranges.Add(new SensitiveRange(match.Index, match.Length));
            categories.Add(category);
        }
    }

    private static string ReplaceRanges(string content, IReadOnlyCollection<SensitiveRange> ranges)
    {
        var merged = new List<SensitiveRange>();
        foreach (var range in ranges.OrderBy(range => range.Start).ThenByDescending(range => range.Length))
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            var end = Math.Max(previous.End, range.End);
            merged[^1] = new SensitiveRange(previous.Start, end - previous.Start);
        }

        var builder = new StringBuilder(content.Length);
        var cursor = 0;
        foreach (var range in merged)
        {
            builder.Append(content, cursor, range.Start - cursor);
            builder.Append(RedactionMarker);
            cursor = range.End;
        }
        builder.Append(content, cursor, content.Length - cursor);
        return builder.ToString();
    }

    private static bool PassesLuhn(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var value = digits[index] - '0';
            if (doubleDigit)
            {
                value *= 2;
                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    private static Regex CreatePattern(string pattern, bool ignoreCase = false) =>
        new(
            pattern,
            RegexOptions.CultureInvariant |
            (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromMilliseconds(100));

    private sealed record SensitiveRange(int Start, int Length)
    {
        public int End => Start + Length;
    }
}

public sealed record SensitiveDataRedactionResult(
    string Text,
    IReadOnlySet<string> Categories,
    bool Changed,
    bool IsComplete);
