namespace SetGameServer.Engine;

public static class Cards
{
    public static readonly int[] Numbers = { 1, 2, 3 };
    public static readonly string[] Shapes = { "diamond", "squiggle", "oval" };
    public static readonly string[] Shadings = { "solid", "striped", "open" };
    public static readonly string[] Colors = { "red", "green", "purple" };

    public static string Id(int number, string shape, string shading, string color)
        => $"{number}-{shape}-{shading}-{color}";

    public static (int number, string shape, string shading, string color) Parse(string id)
    {
        var parts = id.Split('-');
        return (int.Parse(parts[0]), parts[1], parts[2], parts[3]);
    }

    public static bool IsSet(string a, string b, string c)
    {
        var (n1, sh1, sd1, c1) = Parse(a);
        var (n2, sh2, sd2, c2) = Parse(b);
        var (n3, sh3, sd3, c3) = Parse(c);
        bool ok(object x, object y, object z)
            => (x.Equals(y) && y.Equals(z)) || (!x.Equals(y) && !y.Equals(z) && !x.Equals(z));
        return ok(n1, n2, n3) && ok(sh1, sh2, sh3) && ok(sd1, sd2, sd3) && ok(c1, c2, c3);
    }

    public static string WhyNotSet(string a, string b, string c)
    {
        var pa = Parse(a); var pb = Parse(b); var pc = Parse(c);
        bool ok<T>(T x, T y, T z) where T : notnull
            => (x.Equals(y) && y.Equals(z)) || (!x.Equals(y) && !y.Equals(z) && !x.Equals(z));

        string cardCount(int n) => n == 1 ? "one card" : n == 2 ? "two cards" : "three cards";
        string describe<T>(string name, T[] vals) where T : notnull
        {
            var counts = vals.GroupBy(v => v).ToDictionary(g => g.Key!, g => g.Count());
            var parts = counts.Select(kv => name == "number"
                ? $"{kv.Key} {((int)(object)kv.Key! == 1 ? "shape" : "shapes")} on {cardCount(kv.Value)}"
                : $"{kv.Key} on {cardCount(kv.Value)}");
            return string.Join(" and ", parts) + " \u2014 each property must be all the same or all different";
        }

        if (!ok(pa.number, pb.number, pc.number)) return describe("number", new[] { pa.number, pb.number, pc.number });
        if (!ok(pa.shape, pb.shape, pc.shape)) return describe("shape", new[] { pa.shape, pb.shape, pc.shape });
        if (!ok(pa.shading, pb.shading, pc.shading)) return describe("shading", new[] { pa.shading, pb.shading, pc.shading });
        if (!ok(pa.color, pb.color, pc.color)) return describe("color", new[] { pa.color, pb.color, pc.color });
        return "";
    }

    public static List<string> NewShuffledDeck(Random rng)
    {
        var deck = new List<string>(81);
        foreach (var n in Numbers)
            foreach (var sh in Shapes)
                foreach (var sd in Shadings)
                    foreach (var c in Colors)
                        deck.Add(Id(n, sh, sd, c));
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }
}
