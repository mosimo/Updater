using System.Text.RegularExpressions;

namespace R3d.Updater;

/// <summary>
/// Picks the part of a release body that is worth showing in an app. The house release workflow writes
/// <c>## What's new</c>, the tag notes, a <c>---</c> rule and then the install instructions; only the tag notes
/// are for the user of an app that is already installed.
/// </summary>
public static partial class ReleaseNotes
{
    // First line of the install text the release workflows write (design guide, release workflow).
    private const string InstallTextStart = "Self-contained builds";

    /// <summary>
    /// Returns the "What's new" section of <paramref name="body"/>: the text after a "What's new" heading, up to the
    /// first horizontal rule or the next heading of the same or a higher level. Without that heading it returns the
    /// text before the first rule, or the whole body. Returns null when nothing is left, or when the body is only the
    /// install instructions.
    /// </summary>
    public static string? WhatsNew(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int start = 0, end = lines.Length, level = int.MaxValue;
        var heading = Array.FindIndex(lines, l => WhatsNewHeading().IsMatch(l));
        if (heading >= 0)
        {
            start = heading + 1;
            var h = HeadingLevel(lines[heading]);
            if (h > 0) level = h; // a plain "What's new" line ends at any heading
        }
        for (var i = start; i < lines.Length; i++)
        {
            if (Rule().IsMatch(lines[i]) || (heading >= 0 && HeadingLevel(lines[i]) is > 0 and var l && l <= level))
            {
                end = i;
                break;
            }
        }

        var text = string.Join("\n", lines[start..end]).Trim();
        if (text.Length == 0) return null;
        if (heading < 0 && text.StartsWith(InstallTextStart, StringComparison.OrdinalIgnoreCase)) return null;
        return text;
    }

    // ATX heading level 1-6, or 0 when the line is not a heading.
    private static int HeadingLevel(string line)
    {
        var m = AtxHeading().Match(line);
        return m.Success ? m.Groups[1].Length : 0;
    }

    [GeneratedRegex(@"^\s{0,3}(?:#{1,6}\s*)?what['’]?s\s+new\s*:?\s*#*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsNewHeading();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})(?:\s|$)")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^\s{0,3}(?:(?:-\s*){3,}|(?:\*\s*){3,}|(?:_\s*){3,})$")]
    private static partial Regex Rule();
}
