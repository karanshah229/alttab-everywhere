using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AltTabEverywhere;

/// <summary>
/// Type-to-search matching. Pure functions (the caller supplies each window's searchable text),
/// so it can be unit-tested.
///
/// The query is split on spaces and every word must match (so "chr git" finds a Chrome window
/// showing GitHub). Matches are ranked by quality; equal matches keep most-recently-used order.
/// </summary>
internal static class WindowSearch
{
    /// <summary>
    /// Filters and ranks items. For a stack, the best-matching window becomes the one the tile
    /// shows. Returns the items in display order.
    /// </summary>
    public static List<SwitcherItem> Filter(IReadOnlyList<SwitcherItem> items, string query, SearchMode mode,
        Func<WindowInfo, string> searchText)
    {
        var terms = Terms(query);
        if (terms.Length == 0) return items.ToList();

        var ranked = new List<(SwitcherItem Item, int Score, int Order)>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int best = -1, bestWindow = 0;
            for (int w = 0; w < item.Count; w++)
            {
                int score = Score(searchText(item.Windows[w]), terms, mode);
                if (score > best) { best = score; bestWindow = w; }
            }
            if (best < 0) continue;
            item.ShowWindow(bestWindow);
            ranked.Add((item, best, i));
        }
        return ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Order).Select(r => r.Item).ToList();
    }

    public static string[] Terms(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <returns>-1 when some term doesn't match, otherwise a score (higher is better).</returns>
    public static int Score(string text, string[] terms, SearchMode mode)
    {
        int total = 0;
        foreach (string term in terms)
        {
            int score = ScoreTerm(text, term, mode);
            if (score < 0) return -1;
            total += score;
        }
        return total;
    }

    /// <summary>
    /// Contains-match: 1000, plus 200 at the start of a word, minus the position.
    /// Fuzzy (letters in order, gaps allowed): up to ~500, more for letters at word starts and
    /// runs of consecutive letters, less for gaps.
    /// </summary>
    public static int ScoreTerm(string text, string term, SearchMode mode)
    {
        if (term.Length == 0) return 0;
        int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            int score = 1000 - Math.Min(at, 500);
            if (IsWordStart(text, at)) score += 200;
            return score;
        }
        if (mode != SearchMode.Fuzzy) return -1;

        // Greedy subsequence match, preferring word starts when the next letter appears there.
        int position = 0, fuzzy = 500, previous = -2;
        foreach (char c in term)
        {
            int next = IndexOfWordStart(text, c, position);
            if (next < 0) next = IndexOfIgnoreCase(text, c, position);
            if (next < 0) return -1;
            if (IsWordStart(text, next)) fuzzy += 15;
            if (next == previous + 1) fuzzy += 10;
            else fuzzy -= Math.Min(20, next - position);
            previous = next;
            position = next + 1;
        }
        return Math.Max(1, fuzzy);
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]) ||
        (char.IsUpper(text[index]) && char.IsLower(text[index - 1])); // camelCase

    private static int IndexOfIgnoreCase(string text, char c, int start)
    {
        for (int i = start; i < text.Length; i++)
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(c)) return i;
        return -1;
    }

    private static int IndexOfWordStart(string text, char c, int start)
    {
        for (int i = start; i < text.Length; i++)
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(c) && IsWordStart(text, i)) return i;
        return -1;
    }

    /// <summary>
    /// A friendly app name from an app ID or executable path, e.g.
    /// "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" → "WindowsTerminal",
    /// "C:\...\chrome.exe" → "chrome".
    /// </summary>
    public static string AppName(string? appUserModelId, string? processPath)
    {
        var names = new List<string>(2);
        if (!string.IsNullOrEmpty(processPath)) names.Add(Path.GetFileNameWithoutExtension(processPath));
        if (!string.IsNullOrEmpty(appUserModelId))
        {
            string id = appUserModelId;
            int bang = id.IndexOf('!');
            if (bang >= 0)
            {
                // Packaged app: "Name_publisherhash!AppId".
                id = id[..bang];
                int underscore = id.IndexOf('_');
                if (underscore > 0) id = id[..underscore];
            }
            id = id.TrimEnd('\\');
            int slash = id.LastIndexOf('\\');
            if (slash >= 0) id = id[(slash + 1)..];
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
            int dot = id.LastIndexOf('.');
            if (dot >= 0 && dot < id.Length - 1) id = id[(dot + 1)..];
            if (id.All(char.IsDigit)) id = ""; // a version suffix, e.g. "Microsoft.Office.OUTLOOK.EXE.15"
            if (id.Length > 0 && !id.StartsWith('{') && !names.Contains(id, StringComparer.OrdinalIgnoreCase)) names.Add(id);
        }
        return string.Join(' ', names);
    }
}
