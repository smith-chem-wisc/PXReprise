namespace DataRepo.Mcp;

/// <summary>Python's <c>difflib.get_close_matches</c> and the <c>SequenceMatcher</c> ratios it ranks by, ported so
/// that "Did you mean ...?" names the same tables, in the same order, as the Python server.</summary>
public static class Difflib
{
    /// <summary><c>difflib.get_close_matches(word, possibilities, n, cutoff)</c>: the best <paramref name="n"/>
    /// candidates scoring at least <paramref name="cutoff"/>, best first; ties go to the larger string, as
    /// <c>heapq.nlargest</c> over <c>(score, x)</c> orders them.</summary>
    public static List<string> GetCloseMatches(string word, IEnumerable<string> possibilities, int n = 3, double cutoff = 0.6)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), $"n must be > 0: {n}");
        if (cutoff is < 0.0 or > 1.0) throw new ArgumentOutOfRangeException(nameof(cutoff), $"cutoff must be in [0.0, 1.0]: {cutoff}");
        var result = new List<(double Score, string X)>();
        var b = word;
        var b2j = B2J(b);
        var fullbcount = (Dictionary<char, int>?)null;
        foreach (var x in possibilities)
        {
            if (RealQuickRatio(x, b) >= cutoff && QuickRatio(x, b, ref fullbcount) >= cutoff)
            {
                var ratio = Ratio(x, b, b2j);
                if (ratio >= cutoff) result.Add((ratio, x));
            }
        }
        result.Sort((p, q) =>
        {
            var c = q.Score.CompareTo(p.Score);
            return c != 0 ? c : PyValues.CodePointOrder.Compare(q.X, p.X);
        });
        return result.Take(n).Select(r => r.X).ToList();
    }

    private static double CalculateRatio(int matches, int length) => length > 0 ? 2.0 * matches / length : 1.0;

    private static double RealQuickRatio(string a, string b) => CalculateRatio(Math.Min(a.Length, b.Length), a.Length + b.Length);

    private static double QuickRatio(string a, string b, ref Dictionary<char, int>? fullbcount)
    {
        if (fullbcount is null)
        {
            fullbcount = new Dictionary<char, int>();
            foreach (var c in b) fullbcount[c] = fullbcount.GetValueOrDefault(c) + 1;
        }
        var avail = new Dictionary<char, int>();
        var matches = 0;
        foreach (var c in a)
        {
            var numb = avail.TryGetValue(c, out var v) ? v : fullbcount.GetValueOrDefault(c);
            avail[c] = numb - 1;
            if (numb > 0) matches++;
        }
        return CalculateRatio(matches, a.Length + b.Length);
    }

    /// <summary><c>SequenceMatcher.__chain_b</c> with no junk function and <c>autojunk=True</c>: a character
    /// appearing in more than 1% of a sequence of 200 or more is treated as popular and dropped.</summary>
    private static Dictionary<char, List<int>> B2J(string b)
    {
        var b2j = new Dictionary<char, List<int>>();
        for (var i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out var list)) b2j[b[i]] = list = [];
            list.Add(i);
        }
        var n = b.Length;
        if (n >= 200)
        {
            var ntest = n / 100 + 1;
            foreach (var key in b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList()) b2j.Remove(key);
        }
        return b2j;
    }

    private static double Ratio(string a, string b, Dictionary<char, List<int>> b2j)
    {
        var matches = MatchingBlocks(a, b, b2j).Sum(m => m.Size);
        return CalculateRatio(matches, a.Length + b.Length);
    }

    private static List<(int A, int B, int Size)> MatchingBlocks(string a, string b, Dictionary<char, List<int>> b2j)
    {
        var la = a.Length;
        var lb = b.Length;
        var queue = new Stack<(int, int, int, int)>();
        queue.Push((0, la, 0, lb));
        var blocks = new List<(int A, int B, int Size)>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k > 0)
            {
                blocks.Add((i, j, k));
                if (alo < i && blo < j) queue.Push((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
            }
        }
        blocks.Sort((p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B != q.B ? p.B.CompareTo(q.B) : p.Size.CompareTo(q.Size));
        // Collapse adjacent blocks, as Python does; the sum of sizes is all the ratio reads.
        return blocks;
    }

    private static (int, int, int) FindLongestMatch(string a, string b, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var indices))
            {
                foreach (var j in indices)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = j2len.GetValueOrDefault(j - 1) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        // isjunk is None, so nothing is junk and Python's first pair of loops extends the match over any equal
        // characters -- in practice the popular ones autojunk left out of b2j. Its second pair never fires.
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--; bestj--; bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return (besti, bestj, bestsize);
    }
}
