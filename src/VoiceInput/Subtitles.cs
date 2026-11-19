using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceInput;

internal sealed class SubtitleSegment(TimeSpan start, TimeSpan end, string text)
{
    public string StartLabel => Subtitles.FormatTime(Start);
    public string EndLabel => Subtitles.FormatTime(End);
    public TimeSpan Start { get; } = start;
    public TimeSpan End { get; } = end;
    public string Text { get; set; } = text;
}

internal static class Subtitles
{
    internal static List<SubtitleSegment> Parse(string content, bool stripMarkup = true)
    {
        var segments = new List<SubtitleSegment>();
        foreach (var block in Regex.Split(content.ReplaceLineEndings("\n").TrimStart('\uFEFF'), "\n[ \\t]*\n"))
        {
            var lines = block.Split('\n');
            if (lines[0].StartsWith("NOTE", StringComparison.Ordinal)) continue;
            int index = Array.FindIndex(lines, line => line.Contains(" --> ", StringComparison.Ordinal));
            if (index < 0) continue;
            var time = Regex.Split(lines[index].Trim(), @"\s+");
            if (time.Length < 3 || time[1] != "-->" ||
                !TryTime(time[0], out var start) || !TryTime(time[2], out var end) || end <= start)
                continue;
            var text = string.Join("\n", lines[(index + 1)..]).Trim();
            if (stripMarkup) text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
            if (text.Length > 0) segments.Add(new(start, end, text));
        }
        return segments.OrderBy(segment => segment.Start).ToList();
    }

    private static bool TryTime(string value, out TimeSpan time)
    {
        time = default;
        var match = Regex.Match(value, @"^(?:(\d{1,5}):)?([0-5]\d):([0-5]\d)[.,](\d{3})$");
        if (!match.Success) return false;
        var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        time = new TimeSpan(0, hours, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
        return true;
    }

    internal static string FormatTime(TimeSpan time) =>
        FormattableString.Invariant($"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");

    internal static string WriteSrt(IEnumerable<SubtitleSegment> segments)
    {
        var output = new StringBuilder();
        int index = 0;
        foreach (var segment in segments.OrderBy(segment => segment.Start))
        {
            if (segment.Start < TimeSpan.Zero || segment.End <= segment.Start)
                throw new InvalidDataException("Invalid subtitle timing.");
            var lines = segment.Text.ReplaceLineEndings("\n").Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line));
            var text = string.Join(Environment.NewLine, lines);
            if (text.Length == 0) continue;
            output.AppendLine((++index).ToString(CultureInfo.InvariantCulture))
                .AppendLine($"{FormatTime(segment.Start)} --> {FormatTime(segment.End)}")
                .AppendLine(text).AppendLine();
        }
        return output.ToString();
    }
}
