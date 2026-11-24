using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace RonCUT;

public static class SvgImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex NumberRegex = new(@"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    public static List<Contour> Parse(string svg, double unitMm)
    {
        if (!double.IsFinite(unitMm) || unitMm <= 0 || unitMm > 1000)
        {
            throw new InvalidOperationException("Scala SVG non valida.");
        }
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 };
        using var reader = XmlReader.Create(new StringReader(svg), settings);
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName != "svg")
        {
            throw new InvalidOperationException("Il file non contiene un elemento SVG valido.");
        }
        if (doc.Descendants().Any(e => e.Name.LocalName == "style" &&
            e.Ancestors().All(a => (a.Name.NamespaceName.Length == 0 || a.Name.NamespaceName == "http://www.w3.org/2000/svg") &&
                (a == doc.Root || a.Name.LocalName is "g" or "a" or "defs")) && HasCssTransform(e.Value)))
        {
            throw new InvalidOperationException("Le trasformazioni CSS non sono supportate: usa l'attributo SVG transform per posizionare i contorni. Gli altri stili grafici vengono ignorati.");
        }
        var contours = new List<Contour>();
        Walk(doc.Root, Matrix.Identity, unitMm, contours, true);
        if (contours.Count == 0)
        {
            throw new InvalidOperationException("Nessun tracciato supportato trovato nell'SVG.");
        }
        if (contours.Count > 5000 || contours.Sum(c => c.Points.Count) > 500000)
        {
            throw new InvalidOperationException("SVG troppo complesso per questa versione.");
        }
        return contours;
    }

    private static void Walk(XElement element, Matrix parent, double scale, List<Contour> result, bool root = false)
    {
        string name = element.Name.LocalName;
        bool container = name is "g" or "a" || (name == "svg" && root);
        bool shape = name is "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon";
        if ((element.Name.NamespaceName.Length > 0 && element.Name.NamespaceName != "http://www.w3.org/2000/svg") ||
            (!container && !shape))
        {
            // Testi, bitmap, definizioni e oggetti non supportati vengono saltati con tutti i loro figli.
            return;
        }
        string style = (string?)element.Attribute("style") ?? "";
        if (HasCssTransform(style))
        {
            throw new InvalidOperationException("Le trasformazioni CSS non sono supportate: usa l'attributo SVG transform per posizionare i contorni. Gli altri stili grafici vengono ignorati.");
        }
        Matrix local = ParseTransform((string?)element.Attribute("transform"));
        local.Append(parent);
        Geometry? geometry = name switch
        {
            "svg" or "g" or "a" => null,
            "path" => Path(element),
            "rect" => Rectangle(element, scale),
            "circle" => Ellipse(element, scale, true),
            "ellipse" => Ellipse(element, scale, false),
            "line" => Line(element, scale),
            "polyline" => Poly(element, scale, false),
            "polygon" => Poly(element, scale, true),
            _ => null
        };
        if (geometry != null)
        {
            geometry = geometry.Clone();
            local.Scale(scale, scale);
            geometry.Transform = new MatrixTransform(local);
            Rect bounds = geometry.Bounds;
            double minDimension = Math.Min(bounds.Width, bounds.Height);
            double tolerance = minDimension > 0 ? Math.Min(0.02, Math.Max(0.0001, minDimension / 50)) : 0.02;
            var flattened = geometry.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute);
            foreach (var figure in flattened.Figures)
            {
                var contour = new Contour { Id = "c" + result.Count.ToString(Inv), Closed = figure.IsClosed };
                AddPoint(contour, figure.StartPoint);
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        AddPoint(contour, line.Point);
                    }
                    else if (segment is PolyLineSegment lines)
                    {
                        foreach (var p in lines.Points)
                        {
                            AddPoint(contour, p);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Impossibile convertire una curva SVG in segmenti.");
                    }
                }
                if (contour.Closed && contour.Points.Count > 1 && Distance(contour.Points[0], contour.Points[^1]) < 0.0001)
                {
                    contour.Points.RemoveAt(contour.Points.Count - 1);
                }
                if (contour.Points.Count >= (contour.Closed ? 3 : 2))
                {
                    result.Add(contour);
                }
            }
        }
        if (container)
        {
            foreach (var child in element.Elements())
            {
                Walk(child, local, scale, result);
            }
        }
    }

    private static bool HasCssTransform(string css) => Regex.IsMatch(css, @"\b(transform|transform-origin|transform-box)\s*:", RegexOptions.IgnoreCase);

    private static void AddPoint(Contour contour, Point p)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1_000_000 || Math.Abs(p.Y) > 1_000_000)
        {
            throw new InvalidOperationException("Coordinate SVG non valide o troppo grandi.");
        }
        var point = new Pt(p.X, p.Y);
        if (contour.Points.Count == 0 || Distance(contour.Points[^1], point) > 0.00001)
        {
            contour.Points.Add(point);
        }
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static Geometry Path(XElement el)
    {
        string data = (string?)el.Attribute("d") ?? throw new InvalidOperationException("Tracciato SVG senza dati.");
        try
        {
            return Geometry.Parse(data);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Tracciato SVG non supportato: " + ex.Message);
        }
    }

    private static double Length(XElement el, string attr, double scale, double defaultValue = 0)
    {
        string? raw = (string?)el.Attribute(attr);
        if (raw == null)
        {
            return defaultValue;
        }
        var match = Regex.Match(raw.Trim(), @"^\s*([-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?)\s*(mm|cm|in|pt|pc|px)?\s*$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Valore SVG non supportato: {attr}={raw}");
        }
        double n = double.Parse(match.Groups[1].Value, Inv);
        string u = match.Groups[2].Value.ToLowerInvariant();
        return u switch
        {
            "" => n,
            "mm" => n / scale,
            "cm" => n * 10 / scale,
            "in" => n * 25.4 / scale,
            "pt" => n * 25.4 / 72 / scale,
            "pc" => n * 25.4 / 6 / scale,
            "px" => n * 25.4 / 96 / scale,
            _ => throw new InvalidOperationException("Unità SVG non supportata.")
        };
    }

    private static Geometry Rectangle(XElement e, double scale)
    {
        double x = Length(e, "x", scale), y = Length(e, "y", scale);
        double w = Length(e, "width", scale), h = Length(e, "height", scale);
        if (w <= 0 || h <= 0)
        {
            throw new InvalidOperationException("Rettangolo SVG con dimensioni non valide.");
        }
        double rx = Length(e, "rx", scale), ry = Length(e, "ry", scale);
        if (rx == 0)
        {
            rx = ry;
        }
        if (ry == 0)
        {
            ry = rx;
        }
        return new RectangleGeometry(new Rect(x, y, w, h), Math.Min(rx, w / 2), Math.Min(ry, h / 2));
    }

    private static Geometry Ellipse(XElement e, double scale, bool circle)
    {
        double cx = Length(e, "cx", scale), cy = Length(e, "cy", scale);
        double rx = Length(e, circle ? "r" : "rx", scale), ry = circle ? rx : Length(e, "ry", scale);
        if (rx <= 0 || ry <= 0)
        {
            throw new InvalidOperationException("Cerchio o ellisse SVG con dimensioni non valide.");
        }
        return new EllipseGeometry(new Point(cx, cy), rx, ry);
    }

    private static Geometry Line(XElement e, double scale) => new LineGeometry(
        new Point(Length(e, "x1", scale), Length(e, "y1", scale)),
        new Point(Length(e, "x2", scale), Length(e, "y2", scale)));

    private static Geometry Poly(XElement e, double scale, bool closed)
    {
        string raw = (string?)e.Attribute("points") ?? "";
        var nums = Numbers(raw);
        if (nums.Count < 4 || nums.Count % 2 != 0)
        {
            throw new InvalidOperationException("Poligono SVG non valido.");
        }
        var figure = new PathFigure { StartPoint = new Point(nums[0], nums[1]), IsClosed = closed, IsFilled = false };
        for (int i = 2; i < nums.Count; i += 2)
        {
            figure.Segments.Add(new LineSegment(new Point(nums[i], nums[i + 1]), true));
        }
        return new PathGeometry(new[] { figure });
    }

    private static List<double> Numbers(string s)
    {
        var matches = NumberRegex.Matches(s);
        string residue = NumberRegex.Replace(s, "");
        if (residue.Any(c => !char.IsWhiteSpace(c) && c != ','))
        {
            throw new InvalidOperationException("Numeri SVG non validi.");
        }
        return matches.Select(m => double.Parse(m.Value, Inv)).ToList();
    }

    private static Matrix ParseTransform(string? raw)
    {
        Matrix total = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return total;
        }
        var matches = Regex.Matches(raw, @"([A-Za-z]+)\s*\(([^)]*)\)");
        if (matches.Count == 0 || Regex.Replace(raw, @"([A-Za-z]+)\s*\(([^)]*)\)", "").Any(c => !char.IsWhiteSpace(c) && c != ','))
        {
            throw new InvalidOperationException("Trasformazione SVG non valida.");
        }
        foreach (Match item in matches)
        {
            var n = Numbers(item.Groups[2].Value);
            Matrix m = Matrix.Identity;
            switch (item.Groups[1].Value)
            {
                case "matrix" when n.Count == 6:
                    m = new Matrix(n[0], n[1], n[2], n[3], n[4], n[5]);
                    break;
                case "translate" when n.Count is 1 or 2:
                    m.Translate(n[0], n.Count == 2 ? n[1] : 0);
                    break;
                case "scale" when n.Count is 1 or 2:
                    m.Scale(n[0], n.Count == 2 ? n[1] : n[0]);
                    break;
                case "rotate" when n.Count == 1:
                    m.Rotate(n[0]);
                    break;
                case "rotate" when n.Count == 3:
                    m.RotateAt(n[0], n[1], n[2]);
                    break;
                case "skewX" when n.Count == 1:
                    m.Skew(n[0], 0);
                    break;
                case "skewY" when n.Count == 1:
                    m.Skew(0, n[0]);
                    break;
                default:
                    throw new InvalidOperationException("Trasformazione SVG non supportata: " + item.Value);
            }
            total.Prepend(m);
        }
        return total;
    }
}
