using Clipper2Lib;

namespace RonCUT;

public static class PocketGeometry
{
    private const double Epsilon = 0.000001;
    private const long MaxSegmentPairs = 3_000_000;

    public static List<string> SuggestIslands(Job job, Contour outer)
    {
        var outerPath = PathFor(outer);
        var candidates = new List<(Contour Contour, PathD Path)>();
        foreach (var contour in job.Contours)
        {
            if (contour.Id == outer.Id || !contour.Closed || contour.Points.Count < 3)
            {
                continue;
            }
            var path = PathFor(contour);
            if (Inside(path, outerPath))
            {
                candidates.Add((contour, path));
            }
        }

        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            bool insideAnother = candidates.Any(other =>
                other.Contour.Id != candidate.Contour.Id &&
                Math.Abs(Clipper.Area(other.Path)) > Math.Abs(Clipper.Area(candidate.Path)) + Epsilon &&
                Inside(candidate.Path, other.Path));
            if (!insideAnother)
            {
                result.Add(candidate.Contour.Id);
            }
        }
        return result;
    }

    public static PathsD BuildRegion(Job job, Contour outer, IReadOnlyList<string> islandIds)
    {
        var boundary = PathFor(outer);
        Cam.ValidatePolygon(boundary);
        var islands = new List<PathD>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in islandIds)
        {
            if (!seen.Add(id) || id == outer.Id)
            {
                throw new InvalidOperationException("La lista delle isole contiene un contorno duplicato o coincide con la tasca.");
            }
            var island = job.Contours.SingleOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException("Un'isola fa riferimento a un contorno mancante.");
            if (!island.Closed || island.Points.Count < 3)
            {
                throw new InvalidOperationException("L'isola deve essere un contorno chiuso.");
            }
            var islandPath = PathFor(island);
            Cam.ValidatePolygon(islandPath);
            if (!Inside(islandPath, boundary))
            {
                throw new InvalidOperationException("Ogni isola deve trovarsi interamente all'interno della tasca, senza toccarne il bordo.");
            }
            foreach (var other in islands)
            {
                if (BoundariesIntersect(islandPath, other) ||
                    ContainsPoint(islandPath, other[0]) ||
                    ContainsPoint(other, islandPath[0]))
                {
                    throw new InvalidOperationException("Le isole non possono sovrapporsi, toccarsi o essere annidate.");
                }
            }
            islands.Add(islandPath);
        }
        if (islands.Count == 0)
        {
            return new PathsD { boundary };
        }
        var region = Clipper.Difference(new PathsD { boundary }, new PathsD(islands), FillRule.NonZero, 4);
        if (region.Count == 0)
        {
            throw new InvalidOperationException("L'area della tasca è vuota.");
        }
        return region;
    }

    public static PathsD OffsetRegion(PathsD region, double distance)
    {
        return Clipper.InflatePaths(region, -distance, JoinType.Round, EndType.Polygon, 2, 4, 0.001);
    }

    public static bool ContainsContour(Contour inner, Contour outer)
    {
        return inner.Closed && inner.Points.Count >= 3 && Inside(PathFor(inner), PathFor(outer));
    }

    private static PathD PathFor(Contour contour)
    {
        var path = new PathD(contour.Points.Select(p => new PointD(p.X, p.Y)));
        if (!Clipper.IsPositive(path))
        {
            path.Reverse();
        }
        return path;
    }

    private static bool ContainsPoint(PathD polygon, PointD point)
    {
        return Clipper.PointInPolygon(point, polygon, 4) != PointInPolygonResult.IsOutside;
    }

    private static bool Inside(PathD inner, PathD outer)
    {
        if (inner.Count < 3 || outer.Count < 3)
        {
            return false;
        }
        double outerMinX = outer.Min(p => p.x), outerMaxX = outer.Max(p => p.x);
        double outerMinY = outer.Min(p => p.y), outerMaxY = outer.Max(p => p.y);
        if (inner.Any(p => p.x <= outerMinX + Epsilon || p.x >= outerMaxX - Epsilon ||
                           p.y <= outerMinY + Epsilon || p.y >= outerMaxY - Epsilon))
        {
            return false;
        }
        if (inner.Any(p => Clipper.PointInPolygon(p, outer, 4) != PointInPolygonResult.IsInside))
        {
            return false;
        }
        return !BoundariesIntersect(inner, outer);
    }

    private static bool BoundariesIntersect(PathD a, PathD b)
    {
        if ((long)a.Count * b.Count > MaxSegmentPairs)
        {
            throw new InvalidOperationException("I contorni sono troppo complessi per verificare le isole: semplifica il disegno.");
        }
        for (int i = 0; i < a.Count; i++)
        {
            PointD a1 = a[i], a2 = a[(i + 1) % a.Count];
            for (int j = 0; j < b.Count; j++)
            {
                PointD b1 = b[j], b2 = b[(j + 1) % b.Count];
                if (SegmentsIntersect(a1, a2, b1, b2))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(PointD a, PointD b, PointD c, PointD d)
    {
        if (Math.Max(a.x, b.x) + Epsilon < Math.Min(c.x, d.x) ||
            Math.Max(c.x, d.x) + Epsilon < Math.Min(a.x, b.x) ||
            Math.Max(a.y, b.y) + Epsilon < Math.Min(c.y, d.y) ||
            Math.Max(c.y, d.y) + Epsilon < Math.Min(a.y, b.y))
        {
            return false;
        }
        double o1 = Cross(a, b, c), o2 = Cross(a, b, d);
        double o3 = Cross(c, d, a), o4 = Cross(c, d, b);
        if (((o1 > Epsilon && o2 < -Epsilon) || (o1 < -Epsilon && o2 > Epsilon)) &&
            ((o3 > Epsilon && o4 < -Epsilon) || (o3 < -Epsilon && o4 > Epsilon)))
        {
            return true;
        }
        return (Math.Abs(o1) <= Epsilon && OnSegment(a, b, c)) ||
               (Math.Abs(o2) <= Epsilon && OnSegment(a, b, d)) ||
               (Math.Abs(o3) <= Epsilon && OnSegment(c, d, a)) ||
               (Math.Abs(o4) <= Epsilon && OnSegment(c, d, b));
    }

    private static double Cross(PointD a, PointD b, PointD c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    private static bool OnSegment(PointD a, PointD b, PointD p)
    {
        return p.x >= Math.Min(a.x, b.x) - Epsilon && p.x <= Math.Max(a.x, b.x) + Epsilon &&
               p.y >= Math.Min(a.y, b.y) - Epsilon && p.y <= Math.Max(a.y, b.y) + Epsilon;
    }
}
