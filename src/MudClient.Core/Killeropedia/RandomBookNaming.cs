using System.Text.RegularExpressions;
using MudClient.Core.Text;

namespace MudClient.Core.Killeropedia;

/// <summary>
/// Random magic-book ("random 'spellbook'") item names in this MUD are algorithmically built from
/// word pools: an optional value/weight adjective, then a generic noun, then a word that marks
/// which spellcasting class the book belongs to — e.g. "duża księga triumfu" = duża (value-large) +
/// księga (noun) + triumfu (Paladyn). Source: killer.fandom.com/pl/wiki/Losowe_księgi_magiczne_(random_'spellbook').
/// </summary>
public static class RandomBookNaming
{
    public static IReadOnlyList<string> WartoscDuzaWords { get; } =
    [
        "duża", "masywna", "kolosalna", "ogromna", "potężna", "spora", "wielgachna", "znaczna",
        "gigantyczna", "gargantuiczna", "twarda", "pokaźna", "monumentalna", "nowa", "nieużywana",
        "idealna", "okuta", "zdobiona", "wzmocniona", "błyszcząca", "lśniąca", "wielka", "szeroka",
        "tęga", "nowiusieńka",
    ];

    public static IReadOnlyList<string> WartoscMalaWords { get; } =
    [
        "mała", "delikatna", "filigranowa", "nieduża", "drobna", "niepokaźna", "kieszonkowa",
        "stara", "używana", "zniszczona", "sfatygowana", "zdezelowana", "złachmaniona",
        "skomkana", "wysłużona", "podniszczona", "zeszmacona", "wytarta", "zmaltretowana",
        "sparciała", "zaśniedziała", "przetarta",
    ];

    public static IReadOnlyList<string> WagaMalaWords { get; } =
    [
        "lekka", "niewielka", "malutka", "wąska", "lilipucia", "tycia", "cienka", "miękka", "płaska",
    ];

    public static IReadOnlyList<string> NazwaWords { get; } =
    [
        "dzieło", "cymelium", "tom", "inkunabuł", "wolumen", "wolumin", "foliał", "foliant",
        "rękopis", "księga", "książka",
    ];

    public static IReadOnlyList<string> MagWords { get; } =
    [
        "magii", "czarów", "dymu", "kurzu", "sztuczek", "trików", "gestów", "mocy", "woli",
        "inteligencji", "wiedzy", "potęgi", "z pentagramem na okładce", "z rubinami", "z szafirami",
        "z topazami", "z granatami", "z czerwoną wstążeczką",
    ];

    public static IReadOnlyList<string> KlerykWords { get; } =
    [
        "błogosławieństwa", "modlitwy", "Zapomnianego Boga", "wiary", "przekleństwa", "zaufania",
        "medytacji", "skupienia", "sakramentu", "przebaczenia", "uzdrowienia", "odpuszczenia",
    ];

    public static IReadOnlyList<string> PaladynWords { get; } =
    [
        "miecza", "tarczy", "buzdyganu", "włóczni", "siły", "Portena", "walki", "parady",
        "satysfakcji", "triumfu", "zwycięstwa",
    ];

    public static IReadOnlyList<string> DruidWords { get; } =
    [
        "lasu", "puszczy", "roślin", "kwiatów", "zwierząt", "owadów", "oceanów", "rzek",
        "górskich szczytów", "bagna", "piargów", "wiatru",
    ];

    public static IReadOnlyList<string> NomadWords { get; } =
    [
        "piasku", "włóczęgi", "scimitara", "zamieci", "tańca", "słońca", "pogardy",
    ];

    private static readonly IReadOnlyDictionary<string, string> ClassByWord = BuildClassLookup();

    private static readonly Regex Pattern = BuildPattern();

    /// <summary>
    /// Finds "&lt;noun&gt; &lt;class word&gt;" (optionally preceded by a recognized value/weight
    /// adjective) in <paramref name="line"/> and appends " (Klasa)" right after each match. Returns
    /// the line unchanged when nothing matches.
    /// </summary>
    /// <remarks>
    /// Matching runs against a diacritics-folded copy of <paramref name="line"/> rather than the
    /// line itself — the word pools are sourced from a modern, properly-accented wiki page, but
    /// this MUD's server never sends diacritics in its own output, so the pattern is built from
    /// folded words (see <see cref="Alternation"/>) and needs folded input to match. Folding is
    /// 1:1 per character (each Polish diacritic decomposes to exactly one base letter), so a
    /// match's start/length found in the folded copy is still a valid offset into the original —
    /// the annotation is spliced into <paramref name="line"/> itself, never into the folded copy,
    /// so the original text (with or without real diacritics) is preserved verbatim.
    /// </remarks>
    public static string AnnotateClasses(string line)
    {
        var matches = FindMatches(line);
        if (matches is null)
        {
            return line;
        }

        var builder = new System.Text.StringBuilder(line.Length + (matches.Count * 12));
        var lastIndex = 0;
        foreach (Match match in matches)
        {
            var matchEnd = match.Index + match.Length;
            builder.Append(line, lastIndex, matchEnd - lastIndex);
            if (ClassByWord.TryGetValue(match.Groups["classword"].Value, out var className))
            {
                builder.Append(" (").Append(className).Append(')');
            }

            lastIndex = matchEnd;
        }

        builder.Append(line, lastIndex, line.Length - lastIndex);
        return builder.ToString();
    }

    /// <summary>
    /// Returns every recognized random-book name found in <paramref name="line"/>, exactly as it
    /// appears in the original text (diacritics/case preserved, same offsets <see cref="AnnotateClasses"/>
    /// splices its "(Klasa)" suffix at) — the correct literal form to pass straight to a "get
    /// &lt;name&gt;" command. Empty when nothing matches.
    /// </summary>
    public static IReadOnlyList<string> FindBookNames(string line)
    {
        var matches = FindMatches(line);
        if (matches is null)
        {
            return [];
        }

        var names = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            names.Add(line.Substring(match.Index, match.Length));
        }

        return names;
    }

    /// <summary>Shared by <see cref="AnnotateClasses"/> and <see cref="FindBookNames"/> — see
    /// <see cref="AnnotateClasses"/>'s remarks for why matching runs against a folded copy while
    /// the match offsets stay valid against <paramref name="line"/> itself. Null (not an empty
    /// collection) when there's nothing to match, so callers can cheaply short-circuit without
    /// allocating.</summary>
    private static MatchCollection? FindMatches(string line)
    {
        if (!line.Contains(' '))
        {
            return null;
        }

        var folded = PolishText.Fold(line);
        var matches = Pattern.Matches(folded);
        return matches.Count == 0 ? null : matches;
    }

    private static IReadOnlyDictionary<string, string> BuildClassLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddAll(lookup, MagWords, "Mag");
        AddAll(lookup, KlerykWords, "Kleryk");
        AddAll(lookup, PaladynWords, "Paladyn");
        AddAll(lookup, DruidWords, "Druid");
        AddAll(lookup, NomadWords, "Nomad");
        return lookup;
    }

    private static void AddAll(Dictionary<string, string> lookup, IReadOnlyList<string> words, string className)
    {
        foreach (var word in words)
        {
            lookup[PolishText.Fold(word)] = className;
        }
    }

    private static Regex BuildPattern()
    {
        var adjectives = WartoscDuzaWords.Concat(WartoscMalaWords).Concat(WagaMalaWords).Distinct();
        var classWords = MagWords.Concat(KlerykWords).Concat(PaladynWords).Concat(DruidWords).Concat(NomadWords);

        var adjectiveAlternation = Alternation(adjectives);
        var nazwaAlternation = Alternation(NazwaWords);
        var classWordAlternation = Alternation(classWords);

        return new Regex(
            $@"(?:\b(?:{adjectiveAlternation})\s+)?\b(?:{nazwaAlternation})\s+(?<classword>{classWordAlternation})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    // Words are sourced from a modern (properly-accented) wiki page, but this MUD's server never
    // sends diacritics in its own output — fold "księga" to "ksiega" etc. before building the
    // pattern, or it would never match real game text.
    private static string Alternation(IEnumerable<string> phrases) =>
        string.Join(
            '|',
            phrases.Select(PolishText.Fold).OrderByDescending(phrase => phrase.Length).Select(Regex.Escape));
}
