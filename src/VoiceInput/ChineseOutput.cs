using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceInput;

internal static class ChineseOutput
{
    private static readonly Lazy<Dictionary<char, KeyValuePair<string, string>[]>> Simplified = new(() => Load("TS"));
    private static readonly Lazy<Dictionary<char, KeyValuePair<string, string>[]>> Traditional = new(() => Load("ST"));

    internal static string Convert(string text, bool traditional, IEnumerable<string> protectedTerms)
    {
        var exclusions = protectedTerms.Where(term => !string.IsNullOrWhiteSpace(term))
            .OrderByDescending(term => term.Length).Select(Regex.Escape);
        var pattern = string.Join("|", new[] { "\x60[^\x60]*\x60", @"https?://[^\s，。；！？、]+" }.Concat(exclusions));
        var result = new StringBuilder(text.Length);
        int offset = 0;
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant))
        {
            result.Append(Map(text[offset..match.Index], traditional));
            result.Append(match.Value);
            offset = match.Index + match.Length;
        }
        return result.Append(Map(text[offset..], traditional)).ToString();
    }

    private static string Map(string text, bool traditional)
    {
        if (text.Length == 0) return text;
        var dictionary = (traditional ? Traditional : Simplified).Value;
        var output = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length;)
        {
            if (dictionary.TryGetValue(text[index], out var entries))
            {
                var match = entries.FirstOrDefault(entry =>
                    text.AsSpan(index).StartsWith(entry.Key, StringComparison.Ordinal));
                if (match.Key is not null)
                {
                    output.Append(match.Value);
                    index += match.Key.Length;
                    continue;
                }
            }
            output.Append(text[index++]);
        }
        return output.ToString();
    }

    private static Dictionary<char, KeyValuePair<string, string>[]> Load(string direction)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string kind in new[] { "Phrases", "Characters" })
        {
            using var stream = typeof(ChineseOutput).Assembly.GetManifestResourceStream($"VoiceInput.ChineseData.{direction}{kind}.txt")
                ?? throw new InvalidOperationException("Missing Chinese conversion dictionary.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                int tab = line.IndexOf('\t');
                if (tab < 1 || line.StartsWith('#')) continue;
                string target = line[(tab + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (target.Length > 0) entries.TryAdd(line[..tab], target);
            }
        }
        return entries.GroupBy(entry => entry.Key[0]).ToDictionary(group => group.Key,
            group => group.OrderByDescending(entry => entry.Key.Length).ToArray());
    }
}
