namespace RonCUT;

/// <summary>
/// Calcola le parti del percorso del centro fresa da attraversare a quota ponticello.
/// La larghezza richiesta è misurata lungo il percorso; si esclude ogni posizione
/// del centro fresa il cui disco intersecherebbe tale tratto.
/// </summary>
public static class TabGeometry
{
    private const double MergeEpsilon = 0.000001;
    private const double SafetyMarginMm = 0.0002;

    public readonly record struct Interval(double Start, double End);

    public static List<Interval> ExcludedIntervals(List<Pt> path, Operation op, double diameter)
    {
        if (op.Tabs.Count == 0)
        {
            return new List<Interval>();
        }
        if (path.Count < 3 || !double.IsFinite(diameter) || diameter <= 0 ||
            !double.IsFinite(op.TabLength) || op.TabLength <= 0 ||
            !double.IsFinite(op.TabHeight) || op.TabHeight <= 0 || op.TabHeight >= op.Depth)
        {
            throw new InvalidOperationException("Lunghezza e altezza dei ponticelli devono essere positive; l'altezza deve essere minore della profondità.");
        }

        var lengths = CumulativeLengths(path);
        double total = lengths[^1];
        if (op.TabLength + diameter >= total)
        {
            throw new InvalidOperationException("Ponticello troppo lungo per il contorno.");
        }

        var groups = new List<List<Interval>>();
        foreach (var tab in op.Tabs)
        {
            double center = Cam.ClosestDistance(path, tab);
            var desired = Wrap(center - op.TabLength / 2, center + op.TabLength / 2, total);
            var protectedSegments = new List<(Pt A, Pt B)>();
            foreach (var interval in desired)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    double a = Math.Max(interval.Start, lengths[i]);
                    double b = Math.Min(interval.End, lengths[i + 1]);
                    if (b - a <= MergeEpsilon)
                    {
                        continue;
                    }
                    protectedSegments.Add((Interpolate(path, lengths, i, a), Interpolate(path, lengths, i, b)));
                }
            }
            if ((long)protectedSegments.Count * path.Count > 2_000_000)
            {
                throw new InvalidOperationException("Contorno troppo complesso per calcolare il ponticello. Semplifica il tracciato.");
            }

            var excluded = new List<Interval>(desired);
            double radius = diameter / 2 + SafetyMarginMm;
            for (int i = 0; i < path.Count; i++)
            {
                Pt a = path[i], b = path[(i + 1) % path.Count];
                double length = lengths[i + 1] - lengths[i];
                if (length <= MergeEpsilon)
                {
                    continue;
                }
                foreach (var segment in protectedSegments)
                {
                    if (!BoundsOverlap(a, b, segment.A, segment.B, radius))
                    {
                        continue;
                    }
                    foreach (var part in CapsuleIntersection(a, b, segment.A, segment.B, radius))
                    {
                        excluded.Add(new Interval(lengths[i] + length * part.Start, lengths[i] + length * part.End));
                    }
                }
            }
            excluded = Merge(excluded);
            if (!IsSingleCircularInterval(excluded, total))
            {
                throw new InvalidOperationException("Il ponticello è troppo vicino a un altro tratto del percorso: spostalo o usa una fresa più piccola.");
            }
            if (excluded.Sum(i => i.End - i.Start) >= total - MergeEpsilon)
            {
                throw new InvalidOperationException("Il ponticello coprirebbe l'intero contorno.");
            }
            groups.Add(excluded);
        }

        for (int i = 0; i < groups.Count; i++)
        {
            for (int j = i + 1; j < groups.Count; j++)
            {
                if (groups[i].Any(a => groups[j].Any(b => a.Start < b.End + MergeEpsilon && b.Start < a.End + MergeEpsilon)))
                {
                    throw new InvalidOperationException("I ponticelli sono troppo vicini tra loro.");
                }
            }
        }
        return Merge(groups.SelectMany(x => x).ToList());
    }

    public static List<List<Pt>> VisibleSegments(List<Pt> path, IReadOnlyList<Interval> excluded)
    {
        var lengths = CumulativeLengths(path);
        double total = lengths[^1];
        var result = new List<List<Pt>>();
        double cursor = 0;
        foreach (var interval in excluded)
        {
            if (interval.Start > cursor + MergeEpsilon)
            {
                result.Add(Extract(path, lengths, cursor, interval.Start));
            }
            cursor = Math.Max(cursor, interval.End);
        }
        if (cursor < total - MergeEpsilon)
        {
            result.Add(Extract(path, lengths, cursor, total));
        }
        if (result.Count > 1 && excluded.Count > 0 && excluded[0].Start > MergeEpsilon && excluded[^1].End < total - MergeEpsilon)
        {
            var joined = result[^1];
            joined.AddRange(result[0].Skip(1));
            result[0] = joined;
            result.RemoveAt(result.Count - 1);
        }
        return result.Where(x => x.Count >= 2).ToList();
    }

    public static List<double> CumulativeLengths(List<Pt> path)
    {
        var lengths = new List<double> { 0 };
        for (int i = 0; i < path.Count; i++)
        {
            Pt a = path[i], b = path[(i + 1) % path.Count];
            lengths.Add(lengths[^1] + Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)));
        }
        return lengths;
    }

    private static List<Pt> Extract(List<Pt> path, List<double> lengths, double from, double to)
    {
        var result = new List<Pt>();
        for (int i = 0; i < path.Count; i++)
        {
            double a = Math.Max(from, lengths[i]), b = Math.Min(to, lengths[i + 1]);
            if (b - a <= MergeEpsilon)
            {
                continue;
            }
            Pt start = Interpolate(path, lengths, i, a), end = Interpolate(path, lengths, i, b);
            if (result.Count == 0)
            {
                result.Add(start);
            }
            result.Add(end);
        }
        return result;
    }

    private static Pt Interpolate(List<Pt> path, List<double> lengths, int index, double distance)
    {
        Pt a = path[index], b = path[(index + 1) % path.Count];
        double length = lengths[index + 1] - lengths[index];
        double t = length <= MergeEpsilon ? 0 : Math.Clamp((distance - lengths[index]) / length, 0, 1);
        return new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    private static List<Interval> Wrap(double a, double b, double total)
    {
        if (a < 0)
        {
            return new() { new Interval(0, b), new Interval(total + a, total) };
        }
        if (b > total)
        {
            return new() { new Interval(a, total), new Interval(0, b - total) };
        }
        return new() { new Interval(a, b) };
    }

    private static bool BoundsOverlap(Pt a, Pt b, Pt c, Pt d, double r) =>
        Math.Max(a.X, b.X) >= Math.Min(c.X, d.X) - r &&
        Math.Min(a.X, b.X) <= Math.Max(c.X, d.X) + r &&
        Math.Max(a.Y, b.Y) >= Math.Min(c.Y, d.Y) - r &&
        Math.Min(a.Y, b.Y) <= Math.Max(c.Y, d.Y) + r;

    private static List<Interval> CapsuleIntersection(Pt a, Pt b, Pt c, Pt d, double r)
    {
        var parts = new List<Interval>();
        AddCircle(parts, a, b, c, r);
        AddCircle(parts, a, b, d, r);
        double vx = d.X - c.X, vy = d.Y - c.Y;
        double length = Math.Sqrt(vx * vx + vy * vy);
        if (length <= MergeEpsilon)
        {
            return parts;
        }
        vx /= length;
        vy /= length;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double px = a.X - c.X, py = a.Y - c.Y;
        double along0 = px * vx + py * vy;
        double alongDelta = dx * vx + dy * vy;
        double cross0 = px * -vy + py * vx;
        double crossDelta = dx * -vy + dy * vx;
        double lo = 0, hi = 1;
        if (Limit(ref lo, ref hi, along0, alongDelta, 0, length) &&
            Limit(ref lo, ref hi, cross0, crossDelta, -r, r) && hi >= lo)
        {
            parts.Add(new Interval(lo, hi));
        }
        return parts;
    }

    private static void AddCircle(List<Interval> result, Pt a, Pt b, Pt center, double radius)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double ox = a.X - center.X, oy = a.Y - center.Y;
        double aa = dx * dx + dy * dy;
        if (aa <= 1e-20)
        {
            return;
        }
        double bb = 2 * (ox * dx + oy * dy);
        double cc = ox * ox + oy * oy - radius * radius;
        double discriminant = bb * bb - 4 * aa * cc;
        if (discriminant < 0)
        {
            return;
        }
        double root = Math.Sqrt(Math.Max(0, discriminant));
        double lo = Math.Max(0, (-bb - root) / (2 * aa));
        double hi = Math.Min(1, (-bb + root) / (2 * aa));
        if (hi >= lo)
        {
            result.Add(new Interval(lo, hi));
        }
    }

    private static bool Limit(ref double lo, ref double hi, double origin, double delta, double min, double max)
    {
        if (Math.Abs(delta) < 1e-15)
        {
            return origin >= min && origin <= max;
        }
        double a = (min - origin) / delta, b = (max - origin) / delta;
        if (a > b)
        {
            (a, b) = (b, a);
        }
        lo = Math.Max(lo, a);
        hi = Math.Min(hi, b);
        return hi >= lo;
    }

    private static List<Interval> Merge(List<Interval> intervals)
    {
        var result = new List<Interval>();
        foreach (var interval in intervals.OrderBy(x => x.Start).ThenBy(x => x.End))
        {
            if (result.Count == 0 || interval.Start > result[^1].End + MergeEpsilon)
            {
                result.Add(interval);
            }
            else
            {
                var last = result[^1];
                result[^1] = new Interval(last.Start, Math.Max(last.End, interval.End));
            }
        }
        return result;
    }

    private static bool IsSingleCircularInterval(List<Interval> intervals, double total)
    {
        if (intervals.Count == 1)
        {
            return true;
        }
        return intervals.Count == 2 && intervals[0].Start <= MergeEpsilon && intervals[^1].End >= total - MergeEpsilon;
    }
}
