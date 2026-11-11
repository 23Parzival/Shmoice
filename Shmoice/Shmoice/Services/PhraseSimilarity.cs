using System.Text;

namespace Shmoice.Services;

/// <summary>
/// Spots command phrases that sound alike ("pause" / "paws", "next song" / "next one"), which the
/// recognizer can confuse. A rough sound-alike key plus edit distance; good enough to warn about.
/// </summary>
public static class PhraseSimilarity
{
    public record Pair(string A, string B, string Reason);

    public static List<Pair> FindSimilar(IEnumerable<string> phrases)
    {
        var list = phrases.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var pairs = new List<Pair>();
        for (var i = 0; i < list.Count; i++)
        for (var j = i + 1; j < list.Count; j++)
        {
            var (a, b) = (list[i], list[j]);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            {
                pairs.Add(new Pair(a, b, "Same phrase twice: both run (unless they're in different profiles)."));
                continue;
            }
            var ka = SoundKey(a);
            var kb = SoundKey(b);
            if (ka == kb)
                pairs.Add(new Pair(a, b, "These sound the same."));
            else if (Math.Max(ka.Length, kb.Length) >= 4 && Similarity(ka, kb) >= 0.8)
                pairs.Add(new Pair(a, b, "These sound very similar."));
        }
        return pairs;
    }

    /// <summary>Lowercase, letters only, similar-sounding letters merged, vowels dropped after the first letter of each word.</summary>
    private static string SoundKey(string phrase)
    {
        var sb = new StringBuilder();
        foreach (var word in phrase.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new string(word.Where(char.IsLetter).ToArray())
                .Replace("ph", "f").Replace("ck", "k").Replace("qu", "kw").Replace("wh", "w").Replace("kn", "n");
            char last = '\0';
            for (var i = 0; i < w.Length; i++)
            {
                var c = w[i] switch
                {
                    'c' or 'q' => 'k',
                    'z' => 's',
                    'v' => 'f',
                    'd' => 't',
                    'b' => 'p',
                    'g' => 'k',
                    'y' or 'e' or 'i' or 'o' or 'u' or 'a' or 'h' or 'w' => i == 0 ? 'a' : '\0',
                    var other => other,
                };
                if (c == '\0' || c == last) continue;
                sb.Append(c);
                last = c;
            }
            sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static double Similarity(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }
}
