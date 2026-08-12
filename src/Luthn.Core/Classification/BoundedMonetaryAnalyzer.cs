using System.Text.RegularExpressions;

namespace Luthn.Core.Classification;

internal static class BoundedMonetaryAnalyzer
{
    private const int MaximumContextGap = 32;

    private const string NumericQuantity =
        @"(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?(?:\s*(?:bn|k|m|b|천|만|억|조){1,2})?";
    private const string EnglishNumberWord =
        @"(?:zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|billion)";
    private const string EnglishTextQuantity =
        $@"{EnglishNumberWord}(?:[\s-]+{EnglishNumberWord}){{0,7}}";
    private const string KoreanDigitWord = @"[영공일이삼사오육칠팔구]";
    private const string KoreanMagnitudeQuantity =
        @"[영공일이삼사오육칠팔구십백천만억조]{0,15}[십백천만억조][영공일이삼사오육칠팔구십백천만억조]{0,15}";
    private const string AmbiguousKoreanTextQuantity = @"[영공일이삼사오육칠팔구십백천만억조]{2,16}";
    private const string AnyQuantity =
        $@"(?:{NumericQuantity}|{EnglishTextQuantity})";
    private const string AmbiguousQuantity =
        $@"(?:{NumericQuantity}|{EnglishTextQuantity}|{AmbiguousKoreanTextQuantity})";
    private const string CurrencyCode = @"(?:USD|KRW|EUR|JPY|GBP|CNY|RMB)";
    private const string CurrencyName =
        @"(?:원|달러|유로|엔|파운드|위안|dollars?|won|euros?|yen|pounds?|yuan|renminbi)";
    private const string CurrencySymbol = @"[$€£¥₩]";

    private static readonly Regex PrefixedCurrencyAmountPattern = CreatePattern(
        $@"(?:(?<![A-Za-z0-9_]){CurrencySymbol}\s*{AnyQuantity}(?![A-Za-z0-9_])|(?<![A-Za-z0-9_]){CurrencyCode}(?![A-Za-z_])\s+{AnyQuantity}(?![A-Za-z0-9_]))");
    private static readonly Regex SuffixedCurrencyAmountPattern = CreatePattern(
        $@"(?<![A-Za-z0-9_]){AnyQuantity}\s*(?:{CurrencySymbol}|{CurrencyCode}|{CurrencyName})(?![A-Za-z_])");
    private static readonly Regex KoreanTextCurrencyAmountPattern = CreatePattern(
        $@"(?<![가-힣])(?!(?:조\s*원)(?![A-Za-z_]))(?:{KoreanMagnitudeQuantity}\s*|{KoreanDigitWord}\s+)(?:원|달러|유로|엔|파운드|위안)(?![A-Za-z_])");
    private static readonly Regex StrongMonetaryContextPattern = CreatePattern(
        """(?:(?<![A-Za-z])(?:finance|financial\s+records?|revenue|revenues|salary|salaries|wage|wages|payroll|profit|profits|income|earnings|compensation)(?![A-Za-z])|재무|금융\s*정보|매출(?:액)?|판매액|연봉|급여|월급|임금|급료|수익|이익|소득|보수)""");
    private static readonly Regex CompositeMonetaryContextPattern = CreatePattern(
        """(?:(?<![A-Za-z])(?:quote|invoice|payment|contract)\s+(?:amount|value)(?![A-Za-z])|(?:견적|계약|결제|청구)\s*금액)""");
    private static readonly Regex WeakMonetaryContextPattern = CreatePattern(
        """(?:(?<![A-Za-z])(?:amount|amounts|price|prices|cost|costs|budget|budgets|fee|fees)(?![A-Za-z])|금액|금전|가격|판매가|매입가|비용|원가|예산|수수료|단가)""");
    private static readonly Regex AmbiguousQuantityPattern = CreatePattern(
        $@"(?<![A-Za-z0-9_]){AmbiguousQuantity}(?![A-Za-z0-9_])");
    private static readonly Regex ContextConnectorPattern = CreatePattern(
        """^(?:(?:\s|[:=：,()\-–—])|(?:(?:is|was|were|at|about|around|approximately|total|totals|totaled|of|for)\b)|(?:은|는|이|가|을|를|의|에|로|으로|약|대략|총|합계|정도))*$""");

    public static BoundedMonetaryAnalysis Analyze(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return BoundedMonetaryAnalysis.Empty;
        }

        var sensitiveRanges = new List<MonetaryRange>();
        AddMatches(PrefixedCurrencyAmountPattern, content, sensitiveRanges);
        AddMatches(SuffixedCurrencyAmountPattern, content, sensitiveRanges);
        AddMatches(KoreanTextCurrencyAmountPattern, content, sensitiveRanges);
        var exactAmountRanges = sensitiveRanges.ToArray();

        var strongContexts = Matches(StrongMonetaryContextPattern, content);
        var compositeContexts = Matches(CompositeMonetaryContextPattern, content);
        sensitiveRanges.AddRange(strongContexts);
        sensitiveRanges.AddRange(compositeContexts);

        var weakContexts = Matches(WeakMonetaryContextPattern, content)
            .Where(context => !IsCoveredBy(context, compositeContexts))
            .ToArray();
        foreach (var context in weakContexts)
        {
            if (exactAmountRanges.Any(amount => IsBoundedPair(content, context, amount)))
            {
                sensitiveRanges.Add(context);
            }
        }

        var ambiguous = Matches(AmbiguousQuantityPattern, content)
            .Where(quantity => !IsCoveredBy(quantity, exactAmountRanges))
            .Any(quantity => weakContexts.Any(context => IsBoundedPair(content, context, quantity)));

        return new BoundedMonetaryAnalysis(
            sensitiveRanges.Count > 0,
            exactAmountRanges.Length > 0,
            ambiguous,
            sensitiveRanges);
    }

    private static IReadOnlyList<MonetaryRange> Matches(Regex pattern, string content) =>
        pattern.Matches(content)
            .Cast<Match>()
            .Select(match => new MonetaryRange(match.Index, match.Length))
            .ToArray();

    private static void AddMatches(
        Regex pattern,
        string content,
        ICollection<MonetaryRange> ranges)
    {
        foreach (Match match in pattern.Matches(content))
        {
            ranges.Add(new MonetaryRange(match.Index, match.Length));
        }
    }

    private static bool IsCoveredBy(
        MonetaryRange candidate,
        IReadOnlyCollection<MonetaryRange> ranges) =>
        ranges.Any(range => range.Start <= candidate.Start && range.End >= candidate.End);

    private static bool IsBoundedPair(
        string content,
        MonetaryRange first,
        MonetaryRange second)
    {
        var left = first.Start <= second.Start ? first : second;
        var right = first.Start <= second.Start ? second : first;
        var gapLength = right.Start - left.End;
        if (gapLength < 0 || gapLength > MaximumContextGap)
        {
            return false;
        }

        return ContextConnectorPattern.IsMatch(content.Substring(left.End, gapLength));
    }

    private static Regex CreatePattern(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));
}

internal sealed record BoundedMonetaryAnalysis(
    bool HasSensitiveExpression,
    bool HasAmountExpression,
    bool HasAmbiguousExpression,
    IReadOnlyList<MonetaryRange> SensitiveRanges)
{
    public static BoundedMonetaryAnalysis Empty { get; } =
        new(false, false, false, Array.Empty<MonetaryRange>());
}

internal sealed record MonetaryRange(int Start, int Length)
{
    public int End => Start + Length;
}
