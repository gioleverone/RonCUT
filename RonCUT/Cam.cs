using System.Globalization;
using System.Text;
using Clipper2Lib;

namespace RonCUT;

public sealed class Toolpath
{
    public string OperationId { get; set; } = "";
    public double ToolDiameter { get; set; }
    public bool Closed
    {
        get; set;
    }
    public List<Pt> Points { get; set; } = new();
}

public sealed record CutPhase(string Gcode, string ToolLabel, double ToolDiameter);

public static partial class Cam
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<Toolpath> Build(Job job)
    {
        var paths = new List<Toolpath>();
        foreach (var phase in PhaseJobs(job, requireComplete: false))
        {
            var next = BuildPhase(phase);
            foreach (var path in next)
            {
                path.ToolDiameter = phase.Tool.Diameter;
            }
            paths.AddRange(next);
        }
        return paths;
    }

    private static List<Toolpath> BuildPhase(Job job)
    {
        Validate(job);
        var all = new List<Toolpath>();
        foreach (var op in Ordered(job))
        {
            op.IslandContourIds ??= new List<string>();
            var contour = job.Contours.SingleOrDefault(c => c.Id == op.ContourId)
                ?? throw new InvalidOperationException("Un'operazione fa riferimento a un contorno mancante.");
            if (!double.IsFinite(op.Depth) || op.Depth <= 0 || op.Depth > 1000)
            {
                throw new InvalidOperationException("La profondità deve essere maggiore di zero e non superiore a 1000 mm.");
            }
            if (op.Kind != "Incisione" && !contour.Closed)
            {
                throw new InvalidOperationException("Tagli, fori e tasche richiedono contorni chiusi.");
            }
            if (contour.Points.Count < (contour.Closed ? 3 : 2))
            {
                throw new InvalidOperationException("Un contorno non contiene abbastanza punti.");
            }
            if (op.Kind != "Tasca" && op.IslandContourIds.Count > 0)
            {
                throw new InvalidOperationException("Le isole sono disponibili solo per le tasche.");
            }
            if (op.Kind == "Incisione")
            {
                all.Add(new Toolpath { OperationId = op.Id, Closed = contour.Closed, Points = contour.Points.Select(Copy).ToList() });
                continue;
            }
            if (op.Kind == "Foro")
            {
                if (op.Tabs.Count != 0 || op.IslandContourIds.Count != 0)
                {
                    throw new InvalidOperationException("Fori e tasche non supportano i ponticelli; le isole valgono solo per le tasche.");
                }
                var circle = CircleInfo(contour);
                if (circle.Diameter <= job.Tool.Diameter + 0.0001)
                {
                    all.Add(new Toolpath
                    {
                        OperationId = op.Id,
                        Closed = false,
                        Points = new List<Pt> { circle.Center }
                    });
                    continue;
                }
            }
            var source = ToPath(contour.Points);
            ValidatePolygon(source);
            double radius = job.Tool.Diameter / 2;
            if (op.Kind == "Taglio esterno" || op.Kind == "Taglio interno")
            {
                double offset = op.Kind == "Taglio esterno" ? radius : -radius;
                var paths = Offset(source, offset);
                if (offset > 0)
                {
                    // A narrow entrance can close during expansion, leaving an enclosed void.
                    // Follow only the exterior boundary of the compensated contour.
                    paths.RemoveAll(path => !Clipper.IsPositive(path));
                }
                if (paths.Count != 1)
                {
                    throw new InvalidOperationException("Il contorno non può essere compensato con questa fresa oppure si divide in più parti.");
                }
                var result = ToToolpath(op, paths[0]);
                _ = TabGeometry.ExcludedIntervals(result.Points, op, job.Tool.Diameter);
                all.Add(result);
            }
            else if (op.Kind == "Tasca" || op.Kind == "Foro")
            {
                if (op.Tabs.Count != 0)
                {
                    throw new InvalidOperationException("I ponticelli sono disponibili solo per i tagli.");
                }
                PathsD region;
                if (op.Kind == "Foro")
                {
                    region = new PathsD { source };
                }
                else
                {
                    region = PocketGeometry.BuildRegion(job, contour, op.IslandContourIds);
                }
                double step = Math.Min(job.Tool.Diameter * job.Parameters.StepOverPercent / 100, radius);
                if (step < 0.001)
                {
                    throw new InvalidOperationException("Passo laterale troppo piccolo.");
                }
                int rings = 0;
                double islandClearance = op.Kind == "Tasca" && op.IslandContourIds.Count > 0
                    ? Math.Min(0.005, radius * 0.01) : 0;
                while (true)
                {
                    var paths = PocketGeometry.OffsetRegion(region, radius + islandClearance + rings * step);
                    if (paths.Count == 0)
                    {
                        break;
                    }
                    foreach (var path in paths)
                    {
                        all.Add(ToToolpath(op, path));
                    }
                    rings++;
                    if (rings > 20000 || all.Count > 50000)
                    {
                        throw new InvalidOperationException("Tasca troppo complessa; aumentare il passo laterale.");
                    }
                }
                if (rings == 0)
                {
                    throw new InvalidOperationException("La fresa non entra nella tasca o nel foro.");
                }
            }
            else
            {
                throw new InvalidOperationException("Tipo di lavorazione non riconosciuto: " + op.Kind);
            }
        }
        return all;
    }

    public static string Generate(Job job)
    {
        if (job.Operations.Any(operation => operation.IsToolChange))
        {
            throw new InvalidOperationException("Il progetto contiene pause cambio utensile. Aprilo in CUT per eseguire le fasi con la conferma dello zero Z.");
        }
        return GeneratePhase(job, continued: false, final: true);
    }

    private static string GeneratePhase(Job job, bool continued, bool final)
    {
        var paths = BuildPhase(job);
        if (job.Operations.Count == 0)
        {
            throw new InvalidOperationException("Aggiungi almeno una lavorazione prima di esportare.");
        }
        var sb = new StringBuilder();
        sb.AppendLine("(RonCUT - verificare sempre il percorso prima della lavorazione)");
        sb.AppendLine("(Origine e Z zero da impostare sulla macchina)");
        sb.AppendLine("G21");
        sb.AppendLine("G90");
        sb.AppendLine("G17");
        sb.AppendLine("G94");
        if (!continued)
        {
            sb.AppendLine("M3 S" + F(job.Parameters.SpindleRpm));
        }
        sb.AppendLine("G0 Z" + F(job.SafeZ));
        if (continued)
        {
            sb.AppendLine("M3 S" + F(job.Parameters.SpindleRpm));
        }
        else
        {
            sb.AppendLine("G0 X0 Y0");
        }
        foreach (var op in Ordered(job))
        {
            sb.AppendLine("(Lavorazione " + SafeComment(op.Kind) + ")");
            var operationPaths = paths.Where(p => p.OperationId == op.Id).ToList();
            var tabIntervals = operationPaths.ToDictionary(p => p, p => op.Tabs.Count == 0
                ? new List<TabGeometry.Interval>()
                : TabGeometry.ExcludedIntervals(p.Points, op, job.Tool.Diameter));
            double requestedPasses = Math.Ceiling(op.Depth / job.Parameters.StepDown);
            if (!double.IsFinite(requestedPasses) || requestedPasses > 10000)
            {
                throw new InvalidOperationException("Troppe passate in profondità.");
            }
            int passes = (int)requestedPasses;
            bool continuousClosedPath = operationPaths.Count == 1 && (operationPaths[0].Closed || operationPaths[0].Points.Count == 1);
            for (int pass = 1; pass <= passes; pass++)
            {
                double depth = Math.Min(op.Depth, pass * job.Parameters.StepDown);
                foreach (var path in operationPaths)
                {
                    bool positionAtStart = !continuousClosedPath || pass == 1;
                    bool retractAtEnd = !continuousClosedPath || pass == passes;
                    EmitPath(sb, job, op, path, depth, tabIntervals[path], positionAtStart, retractAtEnd);
                }
            }
        }
        sb.AppendLine("G0 Z" + F(job.SafeZ));
        sb.AppendLine("M5");
        if (final)
        {
            sb.AppendLine("M30");
        }
        return sb.ToString();
    }

    private static void EmitPath(StringBuilder sb, Job job, Operation op, Toolpath path, double depth, List<TabGeometry.Interval> tabIntervals, bool positionAtStart, bool retractAtEnd)
    {
        var pts = path.Points;
        if (pts.Count == 1)
        {
            if (positionAtStart)
            {
                sb.AppendLine("G0 Z" + F(job.SafeZ));
                sb.AppendLine("G0 X" + F(pts[0].X - job.ZeroX) + " Y" + F(job.ZeroY - pts[0].Y));
            }
            sb.AppendLine("G1 Z" + F(-depth) + " F" + F(job.Parameters.FeedZ));
            if (retractAtEnd)
            {
                sb.AppendLine("G0 Z" + F(job.SafeZ));
            }
            return;
        }
        if (pts.Count < 2)
        {
            throw new InvalidOperationException("Percorso vuoto.");
        }
        var lengths = new List<double> { 0 };
        int nSegments = path.Closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < nSegments; i++)
        {
            lengths.Add(lengths[^1] + Distance(pts[i], pts[(i + 1) % pts.Count]));
        }
        double total = lengths[^1];
        if (total <= 0.0001)
        {
            throw new InvalidOperationException("Percorso troppo corto.");
        }
        var intervals = new List<(double A, double B)>();
        if (op.Tabs.Count > 0 && depth > op.Depth - op.TabHeight)
        {
            intervals.AddRange(tabIntervals.Select(t => (t.Start, t.End)));
        }
        bool TabAt(double pos) => intervals.Any(t => pos >= t.A - 0.0000001 && pos <= t.B + 0.0000001);
        var breaks = lengths.Concat(intervals.SelectMany(t => new[] { t.A, t.B })).OrderBy(x => x).ToList();
        var positions = new List<double>();
        foreach (double v in breaks)
        {
            if (positions.Count == 0 || v - positions[^1] > 0.0000001)
            {
                positions.Add(v);
            }
        }
        var start = pts[0];
        if (positionAtStart)
        {
            sb.AppendLine("G0 Z" + F(job.SafeZ));
            sb.AppendLine("G0 X" + F(start.X - job.ZeroX) + " Y" + F(job.ZeroY - start.Y));
        }
        bool currentTab = positions.Count > 1 && TabAt((positions[0] + positions[1]) / 2);
        double currentZ = currentTab ? -(op.Depth - op.TabHeight) : -depth;
        sb.AppendLine("G1 Z" + F(currentZ) + " F" + F(job.Parameters.FeedZ));
        int segment = 0;
        for (int i = 0; i < positions.Count - 1; i++)
        {
            double left = positions[i], right = positions[i + 1];
            bool tab = TabAt((left + right) / 2);
            if (tab != currentTab)
            {
                currentZ = tab ? -(op.Depth - op.TabHeight) : -depth;
                sb.AppendLine("G1 Z" + F(currentZ) + " F" + F(job.Parameters.FeedZ));
                currentTab = tab;
            }
            while (segment < nSegments - 1 && right > lengths[segment + 1] + 0.0000001)
            {
                segment++;
            }
            double span = lengths[segment + 1] - lengths[segment];
            double t = span <= 0 ? 0 : (right - lengths[segment]) / span;
            t = Math.Clamp(t, 0, 1);
            Pt a = pts[segment], b = pts[(segment + 1) % pts.Count];
            double x = a.X + (b.X - a.X) * t;
            double y = a.Y + (b.Y - a.Y) * t;
            sb.AppendLine("G1 X" + F(x - job.ZeroX) + " Y" + F(job.ZeroY - y) + " F" + F(job.Parameters.FeedXY));
        }
        if (retractAtEnd)
        {
            sb.AppendLine("G0 Z" + F(job.SafeZ));
        }
    }

    private static IEnumerable<Operation> Ordered(Job job) => job.Operations;

    private static void Validate(Job j)
    {
        Positive(j.Parameters.SpindleRpm, "Giri mandrino", 100000);
        if (j.Version != 1)
        {
            throw new InvalidOperationException("Versione progetto non supportata.");
        }
        Positive(j.Tool.Diameter, "Diametro fresa", 1000);
        Positive(j.Parameters.StepDown, "Avanzamento Z per passata", 1000);
        Positive(j.Parameters.FeedXY, "Avanzamento XY", 100000);
        Positive(j.Parameters.FeedZ, "Velocità discesa Z", 100000);
        Positive(j.SafeZ, "Altezza di sicurezza", 1000);
        if (!double.IsFinite(j.Parameters.StepOverPercent) || j.Parameters.StepOverPercent <= 0 || j.Parameters.StepOverPercent > 100)
        {
            throw new InvalidOperationException("Il passo laterale deve essere compreso tra 0 e 100%.");
        }
        if (!double.IsFinite(j.ZeroX) || !double.IsFinite(j.ZeroY))
        {
            throw new InvalidOperationException("Zero non valido.");
        }
    }

    private static void Positive(double n, string name, double max)
    {
        if (!double.IsFinite(n) || n <= 0 || n > max)
        {
            throw new InvalidOperationException(name + " non valido.");
        }
    }

    private static PathD ToPath(List<Pt> points)
    {
        var p = new PathD(points.Select(x => new PointD(x.X, x.Y)));
        if (!Clipper.IsPositive(p))
        {
            p.Reverse();
        }
        return p;
    }

    internal static void ValidatePolygon(PathD path)
    {
        if (Math.Abs(Clipper.Area(path)) < 0.0001)
        {
            throw new InvalidOperationException("Contorno chiuso senza area.");
        }
        var union = Clipper.Union(new PathsD { path }, new PathsD(), FillRule.NonZero, 4);
        double area = union.Sum(x => Math.Abs(Clipper.Area(x)));
        if (union.Count != 1 || Math.Abs(area - Math.Abs(Clipper.Area(path))) > Math.Max(0.001, area * 0.0001))
        {
            throw new InvalidOperationException("Il contorno si interseca o non è un poligono semplice; correggi l'SVG.");
        }
    }

    private static PathsD Offset(PathD source, double amount) => Clipper.InflatePaths(new PathsD { source }, amount, JoinType.Round, EndType.Polygon, 2, 4, 0.02);

    private static Toolpath ToToolpath(Operation op, PathD p) => new()
    {
        OperationId = op.Id,
        Closed = true,
        Points = p.Select(q => new Pt(q.x, q.y)).ToList()
    };

    public static (Pt Center, double Diameter) CircleInfo(Contour c)
    {
        if (!c.Closed || c.Points.Count < 6)
        {
            throw new InvalidOperationException("Per Foro seleziona un contorno circolare chiuso.");
        }
        double minX = c.Points.Min(p => p.X), maxX = c.Points.Max(p => p.X);
        double minY = c.Points.Min(p => p.Y), maxY = c.Points.Max(p => p.Y);
        double radius = (maxX - minX + maxY - minY) / 4;
        if (radius <= 0 || Math.Abs((maxX - minX) - (maxY - minY)) > Math.Max(0.002, radius * 0.03))
        {
            throw new InvalidOperationException("Per Foro seleziona un contorno circolare chiuso.");
        }
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        if (c.Points.Any(p => Math.Abs(Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) - radius) > Math.Max(0.001, radius * 0.03)))
        {
            throw new InvalidOperationException("Per Foro seleziona un contorno circolare chiuso.");
        }
        return (new Pt(cx, cy), radius * 2);
    }

    public static double ClosestDistance(List<Pt> points, Pt target)
    {
        double best = double.MaxValue, bestDistance = 0, traveled = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Pt a = points[i], b = points[(i + 1) % points.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-10)
            {
                continue;
            }
            double t = Math.Clamp(((target.X - a.X) * dx + (target.Y - a.Y) * dy) / (length * length), 0, 1);
            double ex = a.X + t * dx - target.X, ey = a.Y + t * dy - target.Y;
            double dist = ex * ex + ey * ey;
            if (dist < best)
            {
                best = dist;
                bestDistance = traveled + t * length;
            }
            traveled += length;
        }
        return bestDistance;
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static Pt Copy(Pt p) => new(p.X, p.Y);
    private static string F(double n) => Math.Abs(n) < 0.00005 ? "0" : n.ToString("0.####", Inv);
    private static string SafeComment(string s) => s.Replace("(", "").Replace(")", "").Replace("\r", "").Replace("\n", "");
}
