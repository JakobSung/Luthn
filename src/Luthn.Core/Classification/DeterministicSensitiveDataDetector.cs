using System.Globalization;
using System.Text.RegularExpressions;
using Luthn.Core.Common;

namespace Luthn.Core.Classification;

/// <summary>
/// Detects bounded, high-confidence secret and PII shapes without returning or
/// retaining the matched value. The result contains taxonomy categories only.
/// </summary>
public sealed class DeterministicSensitiveDataDetector
{
    public const string Version = "1";

    private static readonly Regex PrivateKeyPattern = CreatePattern(
        @"-----BEGIN\s+(?:(?:RSA|EC|DSA|OPENSSH)\s+)?PRIVATE\s+KEY-----",
        ignoreCase: true);
    private static readonly Regex AccessTokenPattern = CreatePattern(
        @"(?<![A-Za-z0-9])(?:(?:AKIA|ASIA)[A-Z0-9]{16}|gh[pousr]_[A-Za-z0-9]{30,255}|sk-[A-Za-z0-9_-]{20,255})(?![A-Za-z0-9])");
    private static readonly Regex AccessSecretAssignmentPattern = CreatePattern(
        """(?:api[_ -]?key|access[_ -]?token|secret[_ -]?key|bearer(?:\s+token)?|api\s*키|접근\s*키|액세스\s*키)\s*[:=]\s*['"]?[A-Za-z0-9_./+\-=]{12,255}['"]?""",
        ignoreCase: true);
    private static readonly Regex CredentialAssignmentPattern = CreatePattern(
        """(?:password|passcode|비밀번호|암호(?:\s*번호)?)\s*[:=]\s*['"]?[^\s'"]{8,255}['"]?""",
        ignoreCase: true);
    private static readonly Regex EmailPattern = CreatePattern(
        @"(?<![A-Za-z0-9.!#$%&'*+/=?^_`{|}~-])[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+(?![A-Za-z0-9-])",
        ignoreCase: true);
    private static readonly Regex KoreanPhonePattern = CreatePattern(
        @"(?<!\d)(?:(?:\+82[- ]?1[016789])|(?:01[016789]))[- ]?\d{3,4}[- ]?\d{4}(?!\d)");
    private static readonly Regex KoreanResidentRegistrationPattern = CreatePattern(
        @"(?<!\d)(?<birth>\d{6})-(?<type>[1-8])\d{6}(?!\d)");
    private static readonly Regex PaymentCardCandidatePattern = CreatePattern(
        @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)");

    public ClassificationResult Detect(PublicRecordId sourceId, string? content)
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

            if (CredentialAssignmentPattern.IsMatch(content))
            {
                categories.Add("credential");
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
        }

        return ClassificationResultNormalizer.Normalize(new ClassificationResult(
            sourceId,
            SensitivityLevel.Public,
            categories.Count == 0 ? 0 : 1,
            categories,
            categories.Count > 0));
    }

    private static bool ContainsValidKoreanResidentRegistrationNumber(string content)
    {
        foreach (Match match in KoreanResidentRegistrationPattern.Matches(content))
        {
            var birth = match.Groups["birth"].ValueSpan;
            if (!int.TryParse(birth[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                !int.TryParse(birth.Slice(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
                !int.TryParse(birth.Slice(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            {
                continue;
            }

            var type = match.Groups["type"].ValueSpan[0];
            year += type is '1' or '2' or '5' or '6' ? 1900 : 2000;
            if (DateOnly.TryParseExact(
                $"{year:D4}-{month:D2}-{day:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsLuhnValidPaymentCard(string content)
    {
        Span<char> digits = stackalloc char[19];
        foreach (Match match in PaymentCardCandidatePattern.Matches(content))
        {
            var length = 0;
            foreach (var character in match.ValueSpan)
            {
                if (char.IsAsciiDigit(character))
                {
                    digits[length++] = character;
                }
            }

            if (length is >= 13 and <= 19 && PassesLuhn(digits[..length]))
            {
                return true;
            }
        }

        return false;
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
}
