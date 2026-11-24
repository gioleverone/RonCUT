using System.IO;
using RonCUT;

internal static class SvgImporterTests
{
    public static void Run(string workspace, Action<bool, string> check)
    {
        string gaSvg = File.ReadAllText(Path.Combine(workspace, "Esempi", "GA.svg"));
        var ga = SvgImporter.Parse(gaSvg, 25.4 / 72);
        check(ga.Count == 3 && ga.All(c => c.Closed), "GA.svg originale: importati tutti e tre i contorni chiusi, incluso quello con opacity 0.5");
        double width = ga.SelectMany(c => c.Points).Max(p => p.X) - ga.SelectMany(c => c.Points).Min(p => p.X);
        double height = ga.SelectMany(c => c.Points).Max(p => p.Y) - ga.SelectMany(c => c.Points).Min(p => p.Y);
        check(Math.Abs(width - 174.04715) < 0.001 && height > 223 && height < 224, "GA.svg: dimensioni coerenti con la scala in punti tipografici");
        var opaqueGa = SvgImporter.Parse(gaSvg.Replace("opacity:0.5", "opacity:1"), 25.4 / 72);
        check(SameGeometry(ga, opaqueGa), "GA.svg: la trasparenza non cambia né esclude la geometria");
        var job = new Job
        {
            SvgText = gaSvg,
            Contours = ga,
            ZeroX = ga.SelectMany(c => c.Points).Min(p => p.X),
            ZeroY = ga.SelectMany(c => c.Points).Max(p => p.Y),
            Operations = ga.Select(c => new Operation
            {
                ContourId = c.Id,
                Kind = "Incisione",
                Depth = 1
            }).ToList()
        };
        check(Cam.Build(job).Count == 3 && Cam.Generate(job).Contains("M30"), "GA.svg: i contorni importati sono utilizzabili dal CAM e dall'esportazione software");

        string deerSvg = File.ReadAllText(Path.Combine(workspace, "data", "cerbiatto_cnc.svg"));
        var deer = SvgImporter.Parse(deerSvg, 1);
        var silhouette = deer[0];
        var outside = new Job
        {
            SvgText = deerSvg,
            Contours = deer,
            Tool = new Tool { Diameter = 3 },
            Operations = new List<Operation> { new Operation { ContourId = silhouette.Id, Kind = "Taglio esterno" } }
        };
        var deerPath = Cam.Build(outside).Single();
        double compensation = silhouette.Points.Min(p => p.X) - deerPath.Points.Min(p => p.X);
        check(deer.Count == 8 && deerPath.Closed && Math.Abs(compensation - 1.5) < 0.03
            && Cam.Generate(outside).Contains("M30"),
            "cerbiatto originale a scala millimetri: la fresa da 3 mm genera il solo percorso esterno, compensato di 1,5 mm");

        string decorated = """
            <svg xmlns="http://www.w3.org/2000/svg">
              <defs>
                <style>.trasparente {opacity:0.2; fill:url(#sfumatura); filter:url(#ombra); clip-path:url(#ritaglio); mask:url(#maschera)}</style>
                <linearGradient id="sfumatura"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient>
                <clipPath id="ritaglio"><rect width="1" height="1"/></clipPath>
                <mask id="maschera"><circle r="2"/></mask>
                <filter id="ombra"><feGaussianBlur stdDeviation="2"/></filter>
                <path id="definito" d="M0 0L99 0L99 99Z"/>
              </defs>
              <g transform="translate(10,20) scale(2)" opacity="0.5" clip-path="url(#ritaglio)" mask="url(#maschera)" filter="url(#ombra)">
                <rect x="1" y="2" width="3" height="4" class="trasparente" style="opacity:0;fill-opacity:0.1;stroke-opacity:0.2;filter:url(#ombra)"/>
                <text transform="non-valido">Testo<tspan>ignorato</tspan><path d="non-valido"/></text>
                <image href="https://example.invalid/bitmap.png" transform="non-valido"><path d="non-valido"/></image>
                <foreignObject><style>rect {transform:translate(20px,0)}</style><rect width="300" height="300"/></foreignObject>
                <use href="#definito"/>
                <simboloSconosciuto><rect width="400" height="400"/></simboloSconosciuto>
              </g>
            </svg>
            """;
        var imported = SvgImporter.Parse(decorated, 1);
        check(imported.Count == 1, "SVG misto: testi, bitmap, definizioni e oggetti non supportati non diventano contorni");
        check(imported[0].Points.Min(p => p.X) == 12 && imported[0].Points.Max(p => p.X) == 18 &&
            imported[0].Points.Min(p => p.Y) == 24 && imported[0].Points.Max(p => p.Y) == 32,
            "SVG misto: gradienti, alpha, maschere, filtri e ritagli ignorati; trasformazioni geometriche conservate");
        var hidden = SvgImporter.Parse("<svg><g display='none' visibility='hidden' style='display:none;visibility:hidden;opacity:0'><rect width='3' height='4'/></g></svg>", 1);
        check(hidden.Count == 1, "SVG: gli stili di visibilità vengono ignorati come gli altri effetti grafici");
        var nested = SvgImporter.Parse("<svg><svg x='50' viewBox='0 0 1 1'><rect width='100' height='100'/></svg><rect width='2' height='3'/></svg>", 1);
        check(nested.Count == 1 && nested[0].Points.Max(p => p.X) == 2, "SVG annidato non supportato: saltato senza bloccare i contorni del documento principale");

        foreach (string invalid in new[]
        {
            "<svg><text>Solo testo</text><image href='bitmap.png'/></svg>",
            "<svg><path d='percorso non valido'/></svg>",
            "<svg><rect width='-1' height='3'/></svg>",
            "<svg><rect width='2' height='3' transform='rotate(non-valido)'/></svg>",
            "<svg><rect width='2' height='3' style='transform:translate(20px,0)'/></svg>",
            "<svg><style>rect {transform:translate(20px,0)}</style><rect width='2' height='3'/></svg>"
        })
        {
            bool rejected = false;
            try
            {
                SvgImporter.Parse(invalid, 1);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            check(rejected, "SVG: restano rifiutati documenti privi di geometria, dati geometrici invalidi e trasformazioni CSS non supportate");
        }
    }

    private static bool SameGeometry(List<Contour> first, List<Contour> second)
    {
        return first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Closed == pair.Second.Closed && pair.First.Points.Count == pair.Second.Points.Count &&
            pair.First.Points.Zip(pair.Second.Points).All(points => points.First.X == points.Second.X && points.First.Y == points.Second.Y));
    }
}
