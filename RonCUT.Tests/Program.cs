using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Polyline = System.Windows.Shapes.Polyline;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RonCUT;

internal static class Program
{
    private static int _checks;
    private static readonly string Workspace = FindWorkspace();

    private static string FindWorkspace()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RonCUT", "RonCUT.csproj")) &&
                File.Exists(Path.Combine(directory.FullName, "data", "test.svg")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cartella del progetto RonCUT non trovata: sono necessarie le cartelle RonCUT e data.");
    }
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        Console.WriteLine("OK: " + message);
    }

    [STAThread]
    private static void Main()
    {
        string svg = File.ReadAllText(Path.Combine(Workspace, "data", "test.svg"));
        var contours = SvgImporter.Parse(svg, 25.4 / 72);
        Check(contours.Count == 1 && contours[0].Closed, "SVG reale: rettangolo chiuso");
        var c = contours[0];
        double width = c.Points.Max(p => p.X) - c.Points.Min(p => p.X);
        double height = c.Points.Max(p => p.Y) - c.Points.Min(p => p.Y);
        Check(Math.Abs(width - 49.9886) < .001 && Math.Abs(height - 60.0075) < .001, "SVG reale: dimensioni in mm");
        var transformed = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><g transform='translate(10,20) scale(2)'><rect x='1' y='2' width='3' height='4'/></g></svg>", 1);
        Check(Math.Abs(transformed[0].Points.Min(p => p.X) - 12) < .001 && Math.Abs(transformed[0].Points.Min(p => p.Y) - 24) < .001, "trasformazioni SVG annidate");
        var paths = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><path d='M 0 0 L 10 0 L 10 10 Z M 20 20 C 22 20 24 24 26 20'/><circle cx='50' cy='50' r='5'/></svg>", 1);
        Check(paths.Count == 3 && paths[0].Closed && !paths[1].Closed && paths[2].Closed, "tracciati, curve e cerchi SVG");
        var withText = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><text x='0' y='0'>test</text><rect x='1' y='2' width='3' height='4'/></svg>", 1);
        Check(withText.Count == 1 && withText[0].Closed, "SVG con testo: il testo viene ignorato e la geometria importata");
        SvgImporterTests.Run(Workspace, Check);
        LibraryTests.Run(Check);
        bool rejected = false;

        double minX = c.Points.Min(p => p.X), maxX = c.Points.Max(p => p.X);
        double minY = c.Points.Min(p => p.Y), maxY = c.Points.Max(p => p.Y);
        var op = new Operation
        {
            ContourId = c.Id,
            Kind = "Taglio esterno",
            Depth = 18,
            TabLength = 2,
            TabHeight = 9,
            Tabs = new() { new Pt((minX + maxX) / 2, minY - 1.5), new Pt(minX - 1.5, (minY + maxY) / 2), new Pt(maxX + 1.5, (minY + maxY) / 2) }
        };
        var job = new Job { SvgText = svg, SvgName = "test.svg", Contours = contours, Operations = new() { op }, ZeroX = minX, ZeroY = maxY, SafeZ = 5 };
        var toolpath = Cam.Build(job).Single();
        Check(Math.Abs(toolpath.Points.Min(p => p.X) - (minX - 1.5)) < .01 && Math.Abs(toolpath.Points.Max(p => p.X) - (maxX + 1.5)) < .01, "compensazione esterna 1,5 mm");
        string code = Cam.Generate(job).Replace("\r\n", "\n");
        Check(code.Contains("G21\n") && code.Contains("G90\n") && code.Contains("G17\n") && code.Contains("G94\n"), "unità e modalità esplicite");
        Check(code.Contains("Z-18 ") && code.Contains("Z-9 ") && code.Contains("F250") && code.Contains("F60"), "profondità, ponticelli e avanzamenti nel G-code");
        Check(code.Contains("M3 S18000\n") && code.Contains("G0 Z5\nM5\nM30\n"), "mandrino automatico e spegnimento dopo Z sicurezza");
        job.Parameters.SpindleRpm = 10000;
        Check(Cam.Generate(job).Contains("M3 S10000"), "mandrino: i giri impostati nel progetto raggiungono il G-code");
        job.Parameters.SpindleRpm = 18000;
        Check(code.IndexOf("G90\n", StringComparison.Ordinal) < code.IndexOf("G0 Z5\nG0 X0 Y0\n", StringComparison.Ordinal) &&
              code.IndexOf("G0 Z5\nG0 X0 Y0\n", StringComparison.Ordinal) < code.IndexOf("(Lavorazione Taglio esterno)", StringComparison.Ordinal),
              "inizio: Z di sicurezza e poi X0 Y0 dello zero lavoro");
        string finalMoves = code.Substring(code.LastIndexOf("\nG1 ", StringComparison.Ordinal));
        Check(!finalMoves.Contains("\nG0 X") && finalMoves.Contains("\nG0 Z5\nM5\nM30\n"), "fine: solo risalita e spegnimento, senza ritorno XY");
        int startMoves = code.Split('\n').Count(x => x.StartsWith("G0 X"));
        Check(startMoves == 2, "contorno chiuso: posizionamento iniziale senza ritorni a ogni passata");
        Check(CountClosedDepthPasses(code) == 9, "contorno chiuso: nove passate a profondità progressive");
        int firstCut = code.IndexOf("G1 Z-2 F60", StringComparison.Ordinal);
        int lastCut = code.LastIndexOf("\nG1 ", StringComparison.Ordinal);
        Check(firstCut >= 0 && lastCut > firstCut && !code.Substring(firstCut, lastCut - firstCut).Contains("\nG0 Z"), "contorno chiuso: nessuna risalita a Z sicurezza tra le passate");
        Check(code.Split('\n').Where(x => x.StartsWith("G0 Z")).All(x => !x.Contains("Z-")), "movimenti rapidi Z sopra il piano");
        Check(RapidXYAlwaysSafe(code, 5), "ogni spostamento rapido XY avviene alla Z di sicurezza");
        int tabsAtFinal = CountBridges(code, -18, -9);
        Check(tabsAtFinal >= 3, "tre ponticelli attraversati a quota Z -9 nella passata finale");
        Check(CountFiveMillimeterBridgeMoves(code) == 3, "ponticelli: attraversamento centrale 5 mm per lasciare 2 mm con fresa da 3 mm");
        var restored = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        Check(restored.SvgText == svg && restored.Tool.Diameter == 3 && restored.Operations[0].Tabs.Count == 3 && Cam.Generate(restored).Replace("\r\n", "\n") == code, "salvataggio e riapertura progetto indipendente");
        var legacy = JsonSerializer.Deserialize<Job>(File.ReadAllText(Path.Combine(Workspace, "Esempi", "test-ponticelli.roncut")))!;
        Check(Cam.Generate(legacy).Contains("M3 S18000") && legacy.Operations[0].Tabs.Count == 3, "progetto precedente: riapertura con mandrino automatico");
        string examples = Path.Combine(Path.GetTempPath(), "RonCUT-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(examples);
        TestGeneralSettings(examples);
        var exampleJob = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        new GeneralSettings().Apply(exampleJob);
        File.WriteAllText(Path.Combine(examples, "test-ponticelli-ANTEPRIMA.nc"), Cam.Generate(exampleJob));
        File.WriteAllText(Path.Combine(examples, "test-ponticelli.roncut"), JsonSerializer.Serialize(exampleJob, new JsonSerializerOptions { WriteIndented = true }));
        TestUi(restored, c, minX, minY, maxX, maxY, examples);
        ToolChangeTests.Run(examples, Check);

        var ordered = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(restored))!;
        ordered.Operations.Add(new Operation { ContourId = c.Id, Kind = "Incisione", Depth = 2 });
        string orderedCode = Cam.Generate(ordered);
        Check(orderedCode.IndexOf("(Lavorazione Taglio esterno)", StringComparison.Ordinal) < orderedCode.IndexOf("(Lavorazione Incisione)", StringComparison.Ordinal), "ordine esplicito: taglio esterno prima dell'incisione");
        ordered.Operations.Reverse();
        orderedCode = Cam.Generate(ordered);
        Check(orderedCode.IndexOf("(Lavorazione Incisione)", StringComparison.Ordinal) < orderedCode.IndexOf("(Lavorazione Taglio esterno)", StringComparison.Ordinal), "ordine esplicito: incisione prima del taglio esterno");

        op.Kind = "Taglio interno";
        op.Tabs.Clear();
        var inner = Cam.Build(job).Single();
        Check(Math.Abs(inner.Points.Min(p => p.X) - (minX + 1.5)) < .01, "compensazione interna 1,5 mm");
        op.Kind = "Incisione";
        var engrave = Cam.Build(job).Single();
        Check(Math.Abs(engrave.Points.Min(p => p.X) - minX) < .001, "incisione sul tracciato");
        op.Kind = "Tasca";
        var pocket = Cam.Build(job);
        Check(pocket.Count > 3, "svuotamento tasca con più passate laterali");
        Check(RapidXYAlwaysSafe(Cam.Generate(job), job.SafeZ), "tasca: risalita alla Z di sicurezza per spostarsi tra percorsi distinti");
        bool uncovered = false;
        for (double x = minX + 1.6; x < maxX - 1.6; x += 1.1)
        {
            for (double y = minY + 1.6; y < maxY - 1.6; y += 1.1)
            {
                if (pocket.Min(p => DistanceToPath(p.Points, x, y)) > 1.52)
                {
                    uncovered = true;
                    break;
                }
            }
        }
        Check(!uncovered, "tasca rettangolare: copertura interna a campione con diametro fresa");
        TestPocketIslands(examples);
        var circle = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><circle cx='10' cy='10' r='5'/></svg>", 1);
        job.Contours = circle;
        op.ContourId = circle[0].Id;
        op.Kind = "Foro";
        var hole = Cam.Build(job);
        Check(hole.Count > 1, "foro circolare fresato come tasca");
        Check(hole.Min(p => DistanceToPath(p.Points, 10, 10)) <= 1.5, "foro: copertura del centro");
        job.Tool.Diameter = 20;
        var smallHole = Cam.Build(job).Single();
        Check(smallHole.Points.Count == 1 && Math.Abs(smallHole.Points[0].X - 10) < 0.01 && Math.Abs(smallHole.Points[0].Y - 10) < 0.01, "foro più piccolo della fresa: centrato e grande quanto la fresa");
        Check(RapidXYAlwaysSafe(Cam.Generate(job), job.SafeZ), "foro piccolo: nessuno spostamento rapido XY a profondità di taglio");
        job.Tool.Diameter = 10;
        Check(Cam.Build(job).Single().Points.Count == 1, "foro uguale alla fresa: discesa al centro");
        var tinyContours = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><circle cx='25' cy='40' r='0.05'/></svg>", 1);
        var tinyJob = new Job { Contours = tinyContours, Operations = new() { new Operation { ContourId = tinyContours[0].Id, Kind = "Foro", Depth = 5 } } };
        string tinyCode = Cam.Generate(tinyJob);
        Check(Cam.Build(tinyJob).Single().Points.Count == 1 && tinyCode.Contains("G0 X25 Y-40") && tinyCode.Contains("G1 Z-5 F60"), "foro molto piccolo: discesa verticale al centro fino alla profondità richiesta");
        Check(RapidXYAlwaysSafe(tinyCode, tinyJob.SafeZ) && tinyCode.Split('\n').Count(line => line.StartsWith("G0 X")) == 2, "foro piccolo: nessun movimento laterale durante le passate");
        var equalContours = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><circle cx='5' cy='5' r='1.5'/></svg>", 1);
        var equalJob = new Job { Contours = equalContours, Operations = new() { new Operation { ContourId = equalContours[0].Id, Kind = "Foro" } } };
        Check(Cam.Build(equalJob).Single().Points.Count == 1, "foro SVG da 3 mm e fresa da 3 mm: nessun errore di arrotondamento");
        var squareContours = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><rect x='1' y='1' width='0.1' height='0.1'/></svg>", 1);
        var squareJob = new Job { Contours = squareContours, Operations = new() { new Operation { ContourId = squareContours[0].Id, Kind = "Foro" } } };
        rejected = false;
        try
        {
            Cam.Build(squareJob);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "foro: un rettangolo piccolo non viene scambiato per un cerchio");
        job.Tool.Diameter = 3;
        op.Kind = "Taglio esterno";
        var circlePath = Cam.Build(job).Single();
        op.Tabs.Add(circlePath.Points[0]);
        var curvedTabs = TabGeometry.ExcludedIntervals(circlePath.Points, op, job.Tool.Diameter);
        var curvedCode = Cam.Generate(job);
        Check(curvedTabs.Count == 2 && curvedCode.Contains("Z-9"), "ponticello curvo attraverso l'inizio del percorso");
        Check(ProtectedPathIsSafe(circlePath.Points, op, job.Tool.Diameter, curvedTabs), "ponticello curvo protetto dal disco della fresa");
        Check(TabGeometry.VisibleSegments(circlePath.Points, curvedTabs).Count > 0, "anteprima ponticello curvo con tratto interrotto");
        op.Tabs.Add(circlePath.Points[circlePath.Points.Count / 2]);
        var twoCurvedTabs = TabGeometry.ExcludedIntervals(circlePath.Points, op, job.Tool.Diameter);
        Check(twoCurvedTabs.Count == 3 && Cam.Generate(job).Contains("Z-9"), "due ponticelli curvi separati");
        op.Tabs.Add(circlePath.Points[0]);
        rejected = false;
        try
        {
            Cam.Build(job);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "ponticelli sovrapposti vengono rifiutati");
        op.Tabs.RemoveAt(op.Tabs.Count - 1);

        string demoSvg = "<svg xmlns='http://www.w3.org/2000/svg'><circle cx='30' cy='30' r='20'/><path d='M 22 34 Q 30 40 38 34'/></svg>";
        var demoContours = SvgImporter.Parse(demoSvg, 1);
        var demo = new Job { SvgName = "esempio-curva.svg", SvgText = demoSvg, SvgUnitMm = 1, Contours = demoContours, ZeroX = 10, ZeroY = 50 };
        var curvedCut = new Operation { ContourId = demoContours[0].Id, Kind = "Taglio esterno", Depth = 18, TabLength = 2, TabHeight = 9, Tabs = new() { new Pt(51.5, 30) } };
        demo.Operations.Add(new Operation { ContourId = demoContours[1].Id, Kind = "Incisione", Depth = 2 });
        demo.Operations.Add(curvedCut);
        string demoCode = Cam.Generate(demo);
        File.WriteAllText(Path.Combine(examples, "esempio-curva.roncut"), JsonSerializer.Serialize(demo, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(examples, "esempio-curva-ANTEPRIMA.nc"), demoCode);
        RenderPreview(demo, examples, "anteprima-curva.png");
        Check(demoCode.Contains("M3 S18000") && demoCode.Contains("M5") && demoCode.Contains("Z-9"), "esempio completo curvo esportato con mandrino e ponticello");
        op.Tabs.Clear();
        job.Parameters.StepDown = 0.000001;
        rejected = false;
        try
        {
            Cam.Generate(job);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "numero eccessivo di passate viene rifiutato");
        var openContours = SvgImporter.Parse("<svg xmlns='http://www.w3.org/2000/svg'><line x1='0' y1='0' x2='10' y2='0'/></svg>", 1);
        var openJob = new Job { Contours = openContours, SafeZ = 10, Operations = new() { new Operation { ContourId = openContours[0].Id, Kind = "Incisione", Depth = 4 } } };
        string openCode = Cam.Generate(openJob);
        Check(openCode.Split('\n').Count(line => line.StartsWith("G0 X")) == 3 && RapidXYAlwaysSafe(openCode, 10), "incisione aperta: ritorno al punto iniziale solo a Z di sicurezza");
        Console.WriteLine("Output di prova: " + examples);
        Task.Run(() => GrblTests.Run(Check)).GetAwaiter().GetResult();
        Task.Run(() => JogTests.Run(Check)).GetAwaiter().GetResult();
        Task.Run(() => NotificationTests.Run(Check)).GetAwaiter().GetResult();
        Console.WriteLine("PASS: " + _checks + " verifiche");
    }

    private static void TestPocketIslands(string examples)
    {
        string svg = "<svg xmlns='http://www.w3.org/2000/svg'><rect x='0' y='0' width='40' height='40'/><rect x='15' y='15' width='10' height='10'/></svg>";
        var contours = SvgImporter.Parse(svg, 1);
        var job = new Job { Contours = contours };
        var operation = new Operation { ContourId = contours[0].Id, Kind = "Tasca", Depth = 3, IslandContourIds = new List<string> { contours[1].Id } };
        job.Operations.Add(operation);
        var suggested = PocketGeometry.SuggestIslands(job, contours[0]);
        Check(suggested.SequenceEqual(new[] { contours[1].Id }), "tasca: riconoscimento del contorno interno come isola");
        var paths = Cam.Build(job);
        Check(paths.Count > 2, "tasca con isola: generati percorsi multipli");
        bool islandProtected = true;
        foreach (var path in paths)
        {
            for (int i = 0; i < path.Points.Count; i++)
            {
                var a = path.Points[i];
                var b = path.Points[(i + 1) % path.Points.Count];
                double length = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                int samples = Math.Max(1, (int)Math.Ceiling(length / 0.15));
                for (int n = 0; n <= samples; n++)
                {
                    double t = (double)n / samples;
                    double x = a.X + (b.X - a.X) * t;
                    double y = a.Y + (b.Y - a.Y) * t;
                    if (DistanceToRect(x, y, 15, 15, 25, 25) < 1.49)
                    {
                        islandProtected = false;
                    }
                }
            }
        }
        Check(islandProtected, "tasca con isola: la fresa rimane fuori dall'isola a ogni passaggio");
        bool uncovered = false;
        for (double x = 2; x <= 38; x += 0.8)
        {
            for (double y = 2; y <= 38; y += 0.8)
            {
                if (DistanceToRect(x, y, 15, 15, 25, 25) < 0.35)
                {
                    continue;
                }
                if (paths.Min(p => DistanceToPath(p.Points, x, y)) > 1.53)
                {
                    uncovered = true;
                }
            }
        }
        Check(!uncovered, "tasca con isola: copertura a campione dell'area lavorabile");
        Check(RapidXYAlwaysSafe(Cam.Generate(job), job.SafeZ), "tasca con isola: spostamenti rapidi XY alla Z di sicurezza");
        var restored = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        Check(restored.Operations[0].IslandContourIds.SequenceEqual(operation.IslandContourIds) && Cam.Build(restored).Count == paths.Count, "tasca con isola: contorni conservati nel progetto");
        string crossingSvg = "<svg xmlns='http://www.w3.org/2000/svg'><rect x='0' y='0' width='40' height='40'/><rect x='38' y='10' width='5' height='5'/></svg>";
        var crossing = SvgImporter.Parse(crossingSvg, 1);
        var invalid = new Job { Contours = crossing, Operations = new() { new Operation { ContourId = crossing[0].Id, Kind = "Tasca", IslandContourIds = new() { crossing[1].Id } } } };
        bool rejected = false;
        try
        {
            Cam.Build(invalid);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "tasca con isola: contorno che esce dalla tasca viene rifiutato");
        string multiSvg = "<svg xmlns='http://www.w3.org/2000/svg'><rect x='0' y='0' width='60' height='40'/><rect x='8' y='10' width='8' height='12'/><circle cx='42' cy='20' r='6'/></svg>";
        var multi = SvgImporter.Parse(multiSvg, 1);
        var multiJob = new Job { SvgName = "tasca-due-isole.svg", SvgText = multiSvg, SvgUnitMm = 1, Contours = multi, ZeroY = 40, Operations = new() { new Operation { ContourId = multi[0].Id, Kind = "Tasca", IslandContourIds = new() { multi[1].Id, multi[2].Id } } } };
        Check(PocketGeometry.SuggestIslands(multiJob, multi[0]).Count == 2, "tasca: riconoscimento di due isole distinte");
        var multiPaths = Cam.Build(multiJob);
        bool multiSafe = true;
        foreach (var path in multiPaths)
        {
            for (int i = 0; i < path.Points.Count; i++)
            {
                var a = path.Points[i];
                var b = path.Points[(i + 1) % path.Points.Count];
                int count = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) / 0.1));
                for (int j = 0; j <= count; j++)
                {
                    double t = (double)j / count;
                    double x = a.X + (b.X - a.X) * t;
                    double y = a.Y + (b.Y - a.Y) * t;
                    if (DistanceToRect(x, y, 8, 10, 16, 22) < 1.49 || Math.Sqrt(Math.Pow(x - 42, 2) + Math.Pow(y - 20, 2)) < 7.47)
                    {
                        multiSafe = false;
                    }
                }
            }
        }
        Check(multiSafe && RapidXYAlwaysSafe(Cam.Generate(multiJob), multiJob.SafeZ), "tasca con due isole, una curva: disco utensile e rapidi rispettano le isole");
        File.WriteAllText(Path.Combine(examples, "tasca-due-isole.roncut"), JsonSerializer.Serialize(multiJob, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double DistanceToRect(double x, double y, double left, double top, double right, double bottom)
    {
        double dx = Math.Max(Math.Max(left - x, 0), x - right);
        double dy = Math.Max(Math.Max(top - y, 0), y - bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void TestGeneralSettings(string directory)
    {
        string missingPath = Path.Combine(directory, "impostazioni-assenti.json");
        var defaults = GeneralSettings.Load(missingPath);
        Check(defaults.SafeZ == 10 && defaults.TabLength == 2, "impostazioni generali: valori iniziali 10 e 2 mm");
        Check(defaults.CommunicationsApiUrl == "", "notifiche: disattivate per impostazione iniziale");
        string legacyPath = Path.Combine(directory, "impostazioni-pre-url.json");
        File.WriteAllText(legacyPath, "{\"SafeZ\":10,\"TabLength\":2}");
        var legacySettings = GeneralSettings.Load(legacyPath);
        Check(legacySettings.CommunicationsApiUrl == "", "notifiche: compatibilità con le impostazioni 1.1.0");
        File.WriteAllText(legacyPath, "{\"SafeZ\":12,\"TabLength\":3,\"CutStartUrl\":\"https://example.invalid/start\",\"CutCompleteUrl\":\"https://example.invalid/fine\"}");
        legacySettings = GeneralSettings.Load(legacyPath);
        Check(legacySettings.SafeZ == 12 && legacySettings.TabLength == 3 && legacySettings.CommunicationsApiUrl == "",
            "notifiche: le vecchie impostazioni mantengono i parametri generali e richiedono la scelta del nuovo URL");
        var urlSettings = new GeneralSettings
        {
            CommunicationsApiUrl = "http://example.invalid/api/telegram/?op=send&token=prova"
        };
        string urlsPath = Path.Combine(directory, "impostazioni-url.json");
        File.WriteAllText(urlsPath, JsonSerializer.Serialize(urlSettings));
        var restoredUrls = GeneralSettings.Load(urlsPath);
        Check(restoredUrls.CommunicationsApiUrl == urlSettings.CommunicationsApiUrl, "notifiche: salvataggio e riapertura dell’URL unico con query completa");
        foreach (string invalidUrl in new[] { "non-un-url", "/relativo", "file:///C:/prova", "ftp://example.invalid/prova", "https://example.invalid/\r\nheader" })
        {
            bool invalidRejected = false;
            try
            {
                new GeneralSettings { CommunicationsApiUrl = invalidUrl }.Validate();
            }
            catch (InvalidOperationException)
            {
                invalidRejected = true;
            }
            Check(invalidRejected, "notifiche: URL non HTTP/S o malformato rifiutato (" + invalidUrl.Split(':')[0].Replace("\r", "").Replace("\n", "") + ")");
        }
        string invalidPath = Path.Combine(directory, "impostazioni-incomplete.json");
        File.WriteAllText(invalidPath, "{\"SafeZ\": 12}");
        bool rejected = false;
        try
        {
            GeneralSettings.Load(invalidPath);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "impostazioni generali: un file incompleto non viene accettato silenziosamente");
        rejected = false;
        try
        {
            new GeneralSettings { SafeZ = 0, TabLength = 2 }.Validate();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "impostazioni generali: Z di sicurezza non positiva rifiutata");
        var oldJob = JsonSerializer.Deserialize<Job>(File.ReadAllText(Path.Combine(Workspace, "Esempi", "test-ponticelli.roncut")))!;
        var settings = new GeneralSettings { SafeZ = 11.5, TabLength = 2.75 };
        settings.Apply(oldJob);
        Check(oldJob.SafeZ == 11.5 && oldJob.Operations.All(op => op.TabLength == 2.75), "impostazioni generali: aggiornano anche le lavorazioni di un vecchio progetto");
    }

    private static int CountClosedDepthPasses(string code)
    {
        int count = 0;
        for (int depth = 2; depth <= 18; depth += 2)
        {
            if (code.Contains("G1 Z-" + depth.ToString(CultureInfo.InvariantCulture) + " F60"))
            {
                count++;
            }
        }
        return count;
    }

    private static bool RapidXYAlwaysSafe(string code, double safeZ)
    {
        double? z = null;
        foreach (string line in code.Replace("\r\n", "\n").Split('\n'))
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || (words[0] != "G0" && words[0] != "G1"))
            {
                continue;
            }
            bool xy = words.Skip(1).Any(word => word.StartsWith("X") || word.StartsWith("Y"));
            foreach (string word in words.Skip(1))
            {
                if (word.StartsWith("Z") && double.TryParse(word[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    z = value;
                }
            }
            if (words[0] == "G0" && xy && (!z.HasValue || z.Value < safeZ - 0.0001))
            {
                return false;
            }
        }
        return true;
    }

    private static void RenderPreview(Job job, string directory, string filename)
    {
        var window = new MainWindow(Path.Combine(directory, "preview-settings.json")) { Width = 1450, Height = 900, ShowInTaskbar = false, Left = -5000, Top = -5000 };
        window.Show();
        Type type = typeof(MainWindow);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("_job", flags)!.SetValue(window, job);
        type.GetField("_selected", flags)!.SetValue(window, job.Operations[1]);
        foreach (string name in new[] { "RefreshOperations", "WriteFields", "UpdateLabels", "Fit", "Draw" })
        {
            type.GetMethod(name, flags)!.Invoke(window, null);
        }
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, filename)))
        {
            encoder.Save(file);
        }
        type.GetField("_dirty", flags)!.SetValue(window, false);
        window.Close();
    }

    private static void TestUi(Job saved, Contour contour, double minX, double minY, double maxX, double maxY, string directory)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string settingsPath = Path.Combine(directory, "test-impostazioni-generali.json");
        var window = new MainWindow(settingsPath) { Width = 1450, Height = 900, ShowInTaskbar = false, Left = -5000, Top = -5000 };
        Check(File.Exists(settingsPath) && GeneralSettings.Load(settingsPath).SafeZ == 10 && GeneralSettings.Load(settingsPath).TabLength == 2, "impostazioni generali: i valori iniziali vengono memorizzati al primo avvio");
        window.Show();
        Type type = typeof(MainWindow);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object? value) => type.GetField(name, flags)!.SetValue(window, value);
        void Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(window, args);
        var job = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(saved))!;
        job.Operations.Clear();
        new GeneralSettings().Apply(job);
        Field("_job", job);
        Field("_selected", null);
        Call("UpdateLabels");
        Call("WriteFields");
        Call("Fit");
        var toolPanel = (StackPanel)window.FindName("ToolPanel");
        var selectTool = toolPanel.Children.OfType<Button>().Single(b => (string)b.Tag == "Seleziona");
        var externalTool = toolPanel.Children.OfType<Button>().Single(b => (string)b.Tag == "Taglio esterno");
        var tabTool = toolPanel.Children.OfType<Button>().Single(b => (string)b.Tag == "Ponticelli");
        Check((string?)selectTool.ToolTip == "Strumento attivo", "interfaccia: strumento iniziale evidenziato");
        Call("Mode_Click", externalTool, new RoutedEventArgs());
        Check((string?)externalTool.ToolTip == "Strumento attivo" && selectTool.ToolTip == null, "interfaccia: evidenziazione segue lo strumento attivo");
        Call("HandleClick", new Pt(minX, (minY + maxY) / 2));
        Check(job.Operations.Count == 1 && job.Operations[0].Kind == "Taglio esterno", "interfaccia: assegnazione lavorazione al contorno");
        ((TextBox)window.FindName("DepthBox")).Text = "18";
        ((TextBox)window.FindName("TabHeightBox")).Text = "9,123456";
        Check(job.Operations[0].Depth == 18 && Math.Abs(job.Operations[0].TabHeight - 9.123456) < 1e-10, "interfaccia: modifiche della lavorazione immediatamente acquisite senza arrotondamenti");
        var timer = (System.Windows.Threading.DispatcherTimer)type.GetField("_previewTimer", flags)!.GetValue(window)!;
        Check(timer.IsEnabled, "interfaccia: anteprima aggiornata automaticamente dopo la modifica");
        Call("PreviewTimer_Tick", null, EventArgs.Empty);
        Check(!timer.IsEnabled, "interfaccia: aggiornamento automatico completato");
        Call("Mode_Click", tabTool, new RoutedEventArgs());
        Call("HandleClick", new Pt((minX + maxX) / 2, minY - 1.5));
        Check(job.Operations[0].Depth == 18 && job.Operations[0].Tabs.Count == 1, "interfaccia: modifica profondità e aggiunta ponticello");
        Check(window.FindName("TabLengthBox") == null && window.FindName("SafeZBox") == null, "interfaccia: Z sicurezza e lunghezza ponticelli non sono parametri del progetto");
        var global = new GeneralSettings { SafeZ = 12, TabLength = 3 };
        Call("ApplyGeneralSettings", global);
        var persistedSettings = GeneralSettings.Load(settingsPath);
        Check(persistedSettings.SafeZ == 12 && persistedSettings.TabLength == 3 && job.SafeZ == 12 && job.Operations[0].TabLength == 3, "impostazioni generali: salvate e applicate al progetto aperto");
        Check(Cam.Generate(job).Contains("G0 Z12"), "impostazioni generali: la Z di sicurezza raggiunge il G-code");
        var reopenedWindow = new MainWindow(settingsPath) { ShowInTaskbar = false, Left = -5000, Top = -5000 };
        typeof(MainWindow).GetMethod("LoadProject", flags)!.Invoke(reopenedWindow, new object[] { Path.Combine(Workspace, "Esempi", "test-ponticelli.roncut") });
        var reopenedJob = (Job)typeof(MainWindow).GetField("_job", flags)!.GetValue(reopenedWindow)!;
        Check(reopenedJob.SafeZ == 12 && reopenedJob.Operations.All(o => o.TabLength == 3), "impostazioni generali: persistono al riavvio e sostituiscono i valori dei progetti precedenti");
        Check(Cam.Generate(reopenedJob).Contains("G0 Z12"), "impostazioni generali: i progetti precedenti esportano la Z globale");
        reopenedWindow.Close();
        Call("Mode_Click", selectTool, new RoutedEventArgs());
        Call("Draw");
        Call("HandleClick", new Pt((minX + maxX) / 2, minY - 1.5));
        var selectedField = type.GetField("_selected", flags)!;
        Check(selectedField.GetValue(window) == null && ((StackPanel)window.FindName("OperationDetails")).Visibility == Visibility.Collapsed, "interfaccia: clic nel vuoto del ponticello deseleziona e nasconde i parametri");
        Call("HandleClick", new Pt(minX - 1.5, (minY + maxY) / 2));
        Check(ReferenceEquals(selectedField.GetValue(window), job.Operations[0]) && ((ListBox)window.FindName("OperationsList")).SelectedItem is ListBoxItem, "interfaccia: clic sulla fascia compensata seleziona la lavorazione e la lista");
        Check(Math.Abs(job.Operations[0].TabHeight - 9.123456) < 1e-10, "interfaccia: selezione non arrotonda né altera i parametri");
        var depthInput = (TextBox)window.FindName("DepthBox");
        depthInput.Text = "";
        bool blockedInvalid = false;
        try
        {
            Call("SyncFields");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            blockedInvalid = true;
        }
        Check(blockedInvalid && job.Operations[0].Depth == 18, "interfaccia: input temporaneamente vuoto non viene salvato come valore valido");
        depthInput.Text = "18";
        ((TextBox)window.FindName("StepDownBox")).Text = "1,75";
        Check(Math.Abs(job.Parameters.StepDown - 1.75) < 1e-10, "interfaccia: parametri del lavoro aggiornati automaticamente");
        string autosaveTestPath = Path.Combine(directory, "progetto-con-modifiche.roncut");
        Field("_filePath", autosaveTestPath);
        bool savedOk = (bool)type.GetMethod("Save", flags)!.Invoke(window, new object[] { false })!;
        var savedChanges = JsonSerializer.Deserialize<Job>(File.ReadAllText(autosaveTestPath))!;
        using (var projectDocument = JsonDocument.Parse(File.ReadAllText(autosaveTestPath)))
        {
            Check(!projectDocument.RootElement.TryGetProperty("SafeZ", out _) && !projectDocument.RootElement.GetProperty("Operations")[0].TryGetProperty("TabLength", out _), "salvataggio: le impostazioni generali non sono memorizzate nel progetto");
        }
        persistedSettings.Apply(savedChanges);
        Check(savedOk && savedChanges.SafeZ == 12 && savedChanges.Operations[0].TabLength == 3 && Math.Abs(savedChanges.Operations[0].TabHeight - 9.123456) < 1e-10 && Math.Abs(savedChanges.Parameters.StepDown - 1.75) < 1e-10, "salvataggio: il progetto riprende i valori globali e conserva i valori propri");
        var shape = window.FindName("SizeLabel") as TextBlock;
        Check(shape != null && shape.Text.Contains(50.0.ToString("0.0", CultureInfo.CurrentCulture)) && shape.Text.Contains(60.0.ToString("0.0", CultureInfo.CurrentCulture)), "interfaccia: dimensioni arrotondate al decimo");
        var incision = new Operation { ContourId = contour.Id, Kind = "Incisione", Depth = 2 };
        job.Operations.Add(incision);
        Call("RefreshOperations");
        Call("Draw");
        Call("HandleClick", new Pt(minX, (minY + maxY) / 2));
        Check(ReferenceEquals(selectedField.GetValue(window), incision), "interfaccia: selezione distingue incisione e taglio vicini");
        Call("HandleClick", new Pt(minX - 1.5, (minY + maxY) / 2));
        Check(ReferenceEquals(selectedField.GetValue(window), job.Operations[0]), "interfaccia: clic sulla fascia individua il taglio corretto");
        Call("HandleClick", new Pt(minX, (minY + maxY) / 2));
        var depthColor = type.GetMethod("DepthColor", flags)!;
        Check(!Equals(depthColor.Invoke(window, new object[] { 2.0 }), depthColor.Invoke(window, new object[] { 18.0 })), "interfaccia: profondità diverse hanno colori diversi");
        Call("MoveOperation", -1);
        Check(job.Operations[0] == incision && job.Operations[1].Kind == "Taglio esterno", "interfaccia: spostamento modifica la sequenza");
        var drawing = (Canvas)window.FindName("Drawing");
        Check(drawing.Children.OfType<Polyline>().Any(p => p.Stroke is SolidColorBrush brush && brush.Color.R == 255 && brush.Color.G == 183), "interfaccia: lavorazione selezionata evidenziata");
        var focusBox = (TextBox)window.FindName("DepthBox");
        Keyboard.Focus(focusBox);
        var source = PresentationSource.FromVisual(window)!;
        var delete = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        if (Keyboard.FocusedElement == focusBox)
        {
            Call("Window_PreviewKeyDown", window, delete);
            Check(job.Operations.Count == 2, "tasto CANC: la scrittura nel campo non cancella la lavorazione");
        }
        Keyboard.Focus((ListBox)window.FindName("OperationsList"));
        delete = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Call("Window_PreviewKeyDown", window, delete);
        Check(job.Operations.Count == 1 && job.Operations[0].Kind == "Taglio esterno", "tasto CANC: elimina la lavorazione selezionata");

        var pocketJob = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(saved))!;
        pocketJob.Operations.Clear();
        var pocketOperation = new Operation { ContourId = contour.Id, Kind = "Tasca", Depth = 2 };
        pocketJob.Operations.Add(pocketOperation);
        Field("_job", pocketJob);
        Field("_selected", null);
        Call("RefreshOperations");
        Call("WriteFields");
        Call("Draw");
        var pocketPaths = Cam.Build(pocketJob);
        var interiorPoint = pocketPaths[pocketPaths.Count / 2].Points[0];
        Call("HandleClick", interiorPoint);
        Check(ReferenceEquals(selectedField.GetValue(window), pocketOperation), "interfaccia: clic su un percorso interno della tasca seleziona la tasca");

        string islandSvg = "<svg xmlns='http://www.w3.org/2000/svg'><rect x='0' y='0' width='40' height='40'/><rect x='15' y='15' width='10' height='10'/></svg>";
        var islandContours = SvgImporter.Parse(islandSvg, 1);
        var islandJob = new Job { SvgText = islandSvg, SvgName = "isola.svg", Contours = islandContours };
        Field("_job", islandJob);
        Field("_selected", null);
        Call("WriteFields");
        var pocketTool = toolPanel.Children.OfType<Button>().Single(b => (string)b.Tag == "Tasca");
        var islandTool = toolPanel.Children.OfType<Button>().Single(b => (string)b.Tag == "Isole");
        Call("Mode_Click", pocketTool, new RoutedEventArgs());
        Call("HandleClick", new Pt(0, 20));
        Check(islandJob.Operations.Count == 1 && islandJob.Operations[0].IslandContourIds.SequenceEqual(new[] { islandContours[1].Id }), "interfaccia: isola riconosciuta creando la tasca");
        Check(((TextBlock)window.FindName("SelectedLabel")).Text.Contains("1 isole"), "interfaccia: numero di isole visibile");
        Call("Mode_Click", islandTool, new RoutedEventArgs());
        Call("HandleClick", new Pt(15, 20));
        Check(islandJob.Operations[0].IslandContourIds.Count == 0, "interfaccia: clic rimuove l'isola");
        Call("HandleClick", new Pt(15, 20));
        Check(islandJob.Operations[0].IslandContourIds.SequenceEqual(new[] { islandContours[1].Id }), "interfaccia: secondo clic ripristina l'isola");

        string smallSvg = "<svg xmlns='http://www.w3.org/2000/svg'><circle cx='25' cy='40' r='0.5'/></svg>";
        var smallContours = SvgImporter.Parse(smallSvg, 1);
        var smallJob = new Job { SvgText = smallSvg, SvgName = "foro.svg", Contours = smallContours };
        Field("_job", smallJob);
        Field("_selected", null);
        Call("WriteFields");
        var holeTool = toolPanel.Children.OfType<Button>().Single(b => (string)b.Tag == "Foro");
        Call("Mode_Click", holeTool, new RoutedEventArgs());
        Call("HandleClick", new Pt(25.5, 40));
        Check(smallJob.Operations.Count == 1 && ((TextBlock)window.FindName("SelectedLabel")).Text.Contains("3,0 mm".Replace(',', CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0])), "interfaccia: foro piccolo mostra il diametro effettivo della fresa");
        Check(drawing.Children.OfType<System.Windows.Shapes.Ellipse>().Any(e => Math.Abs(e.Width - 21) < 0.001), "interfaccia: foro piccolo mostrato come disco della fresa");
        Call("Mode_Click", selectTool, new RoutedEventArgs());
        Field("_selected", null);
        Call("Draw");
        Call("HandleClick", new Pt(25, 40));
        Check(ReferenceEquals(selectedField.GetValue(window), smallJob.Operations[0]), "interfaccia: il foro piccolo è selezionabile nel disegno");

        var messageType = typeof(MainWindow).Assembly.GetType("RonCUT.LargeMessageWindow")!;
        var message = (Window)Activator.CreateInstance(messageType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { window, "Progetto salvato", "File salvato", false }, null)!;
        var layout = (Grid)message.Content;
        var messageButtons = layout.Children.OfType<StackPanel>().Single();
        Check(message.Width >= 700 && message.Height >= 320 && messageButtons.Children.OfType<Button>().All(b => b.Height >= 50 && b.FontSize >= 18), "interfaccia: conferme grandi con pulsanti leggibili");

        var libraryWindow = new LibraryWindow(Library.CreateDefault());
        var toolGrid = (DataGrid)typeof(LibraryWindow).GetField("_tools", flags)!.GetValue(libraryWindow)!;
        var editing = (IEditableCollectionView)toolGrid.Items;
        Check(editing.CanAddNew, "interfaccia archivio: aggiunta di utensili disponibile");
        var newTool = (Tool)editing.AddNew();
        newTool.Name = "Fresa prova";
        newTool.Diameter = 2;
        editing.CommitNew();
        Check(libraryWindow.Value.Tools.Any(t => t.Name == "Fresa prova" && t.Diameter == 2 && t.Id != Guid.Empty), "interfaccia archivio: nuovo utensile registrato con un identificativo");
        libraryWindow.Close();
        var generalWindow = new GeneralSettingsWindow(new GeneralSettings()) { ShowInTaskbar = false, Left = -5000, Top = -5000 };
        generalWindow.Show();
        generalWindow.UpdateLayout();
        var safeBox = (TextBox)typeof(GeneralSettingsWindow).GetField("_safeZ", flags)!.GetValue(generalWindow)!;
        var lengthBox = (TextBox)typeof(GeneralSettingsWindow).GetField("_tabLength", flags)!.GetValue(generalWindow)!;
        Check(safeBox.Text == "10" && lengthBox.Text == "2", "finestra impostazioni generali: valori iniziali visibili");
        var apiUrlBox = (TextBox)typeof(GeneralSettingsWindow).GetField("_communicationsApiUrl", flags)!.GetValue(generalWindow)!;
        apiUrlBox.Text = " http://example.invalid/api/telegram/?op=send&token=" + new string('a', 180) + " ";
        generalWindow.UpdateLayout();
        Check(apiUrlBox.TextWrapping == TextWrapping.Wrap && apiUrlBox.LineCount > 1,
            "interfaccia notifiche: un indirizzo lungo va a capo nel campo a più righe");
        var editedSettings = (GeneralSettings)typeof(GeneralSettingsWindow).GetMethod("ReadSettings", flags)!.Invoke(generalWindow, null)!;
        Check(editedSettings.CommunicationsApiUrl == apiUrlBox.Text.Trim(), "interfaccia notifiche: l’indirizzo visualizzato su più righe resta un unico URL");
        Call("ApplyGeneralSettings", editedSettings);
        Check(GeneralSettings.Load(settingsPath).CommunicationsApiUrl == editedSettings.CommunicationsApiUrl, "interfaccia notifiche: l’URL viene salvato nelle impostazioni generali");
        generalWindow.UpdateLayout();
        var generalBitmap = new RenderTargetBitmap((int)generalWindow.ActualWidth, (int)generalWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        generalBitmap.Render(generalWindow);
        var generalEncoder = new PngBitmapEncoder();
        generalEncoder.Frames.Add(BitmapFrame.Create(generalBitmap));
        using (var file = File.Create(Path.Combine(directory, "impostazioni-generali.png")))
        {
            generalEncoder.Save(file);
        }
        generalWindow.Close();
        string cutSettingsPath = Path.Combine(directory, "impostazioni-cut.json");
        bool jogKeyHeld = true;
        var cutWindow = new CutWindow(Cam.Generate(saved), saved.Tool.Diameter, cutSettingsPath, jogKeyIsDown: _ => jogKeyHeld) { Width = 1450, Height = 900, ShowInTaskbar = false, Left = -5000, Top = -5000 };
        cutWindow.Show();
        cutWindow.UpdateLayout();
        var slow = (RadioButton)cutWindow.FindName("SlowJogButton");
        var fast = (RadioButton)cutWindow.FindName("FastJogButton");
        var profiles = (System.Windows.Controls.Primitives.UniformGrid)cutWindow.FindName("JogProfileButtons");
        Check(profiles.Children.Count == 2 && slow.IsChecked == true && fast.IsChecked == false, "CUT: due profili fissi, Slow predefinito");
        var startButton = (Button)cutWindow.FindName("StartButton");
        var simulateButton = (Button)cutWindow.FindName("SimulateButton");
        Check(!startButton.IsEnabled && simulateButton.IsEnabled, "CUT: avvio bloccato senza connessione e simulatore disponibile");
        var previewHost = (Grid)cutWindow.FindName("PreviewHost");
        var preview3d = previewHost.Children.OfType<CutPreview>().Single();
        int withoutTool = preview3d.Children.Count;
        preview3d.ShowPosition(new MachinePosition(10, 10, 5), true);
        Check(preview3d.Children.Count == withoutTool + 1 && preview3d.NominalSeconds > 0, "CUT: utensile 3D appare alla posizione indicata e tempo stimato disponibile");
        CutPreviewTests.Run(cutWindow, preview3d, Check);
        preview3d.SetView("top");
        var cutBitmap = new RenderTargetBitmap((int)cutWindow.ActualWidth, (int)cutWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        cutBitmap.Render(cutWindow);
        var cutEncoder = new PngBitmapEncoder();
        cutEncoder.Frames.Add(BitmapFrame.Create(cutBitmap));
        using (var file = File.Create(Path.Combine(directory, "RonCUT-CUT.png")))
        {
            cutEncoder.Save(file);
        }
        preview3d.SetView("iso");
        var isoBitmap = new RenderTargetBitmap((int)cutWindow.ActualWidth, (int)cutWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        isoBitmap.Render(cutWindow);
        var isoEncoder = new PngBitmapEncoder();
        isoEncoder.Frames.Add(BitmapFrame.Create(isoBitmap));
        using (var file = File.Create(Path.Combine(directory, "RonCUT-CUT-assonometria.png")))
        {
            isoEncoder.Save(file);
        }
        preview3d.SetView("top");
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(cutWindow.Dispatcher));
        simulateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => startButton.IsEnabled, 5000);
        fast.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(fast.IsChecked == true && slow.IsChecked == false && CutSettings.Load(cutSettingsPath).SelectedProfile == "Fast",
            "CUT: selezione Fast evidenziata e salvata automaticamente");
        var connectionLabel = (TextBlock)cutWindow.FindName("ConnectionLabel");
        Check(connectionLabel.Text.Contains("SIMULAZIONE"), "CUT: il simulatore è chiaramente identificato nell'interfaccia");
        var controllerField = typeof(CutWindow).GetField("_controller", flags)!;
        var activeController = (GrblController)controllerField.GetValue(cutWindow)!;
        JogUiTests.Run(cutWindow, activeController, directory, Check, value => jogKeyHeld = value);
        startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => activeController.IsRunning && activeController.SpindleRunning, 5000);
        Check(activeController.WorkPosition != null, "CUT: avvio simulato aggiorna mandrino e posizione nella vista");
        var pauseButton = (Button)cutWindow.FindName("PauseButton");
        pauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => activeController.IsPaused, 4000);
        Check((pauseButton.Content?.ToString() ?? "").Contains("RIPRENDI"), "CUT: pausa diventa Riprendi nella simulazione");
        ((Button)cutWindow.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !activeController.IsRunning && !activeController.IsResetAwaitingConfirmation, 4000);
        Check(!activeController.IsRunning, "CUT: STOP interrompe la simulazione tramite interfaccia");
        cutWindow.Close();
        SynchronizationContext.SetSynchronizationContext(previousContext);
        var storedCut = CutSettings.Load(cutSettingsPath);
        var settingsWindow = new CutSettingsWindow(storedCut, cutSettingsPath) { ShowInTaskbar = false, Left = -5000, Top = -5000 };
        settingsWindow.Show();
        var stepEditor = (TextBox)typeof(CutSettingsWindow).GetField("_fastStep", flags)!.GetValue(settingsWindow)!;
        var slowFeedEditor = (TextBox)typeof(CutSettingsWindow).GetField("_slowFeed", flags)!.GetValue(settingsWindow)!;
        slowFeedEditor.Text = "125";
        stepEditor.Text = "0,25";
        var editedCut = CutSettings.Load(cutSettingsPath);
        Check(editedCut.Fast.Step == 0.25 && editedCut.Slow.Feed == 125 && editedCut.Fast.Feed == storedCut.Fast.Feed,
            "CUT: passo e feed modificabili separatamente per entrambe le modalità");
        stepEditor.Text = "";
        Check(CutSettings.Load(cutSettingsPath).Fast.Step == 0.25, "CUT: dato incompleto non sostituisce il profilo valido salvato");
        settingsWindow.Close();
        CutConnectionTests.Run(directory, Check);
        Field("_job", saved);
        Field("_selected", saved.Operations[0]);
        Call("RefreshOperations");
        Call("WriteFields");
        Call("UpdateLabels");
        Call("Fit");
        Call("Draw");
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, "anteprima-RonCUT.png")))
        {
            encoder.Save(file);
        }
        Field("_dirty", false);
        window.Close();
        BatchOperationTests.Run(directory, Check);
    }

    internal static void PumpUntil(Func<bool> condition, int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        poll.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= end)
            {
                poll.Stop();
                frame.Continue = false;
            }
        };
        poll.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (!condition())
        {
            throw new TimeoutException("L'interfaccia CUT non ha raggiunto lo stato atteso.");
        }
    }

    private static int CountBridges(string code, double cutDepth, double tabDepth)
    {
        var lines = code.Split('\n');
        double z = 0;
        bool inFinal = false;
        int count = 0;
        foreach (string line in lines)
        {
            if (!line.StartsWith("G1 Z"))
            {
                continue;
            }
            string word = line.Split(' ')[1].Substring(1);
            double next = double.Parse(word, CultureInfo.InvariantCulture);
            if (next == cutDepth)
            {
                inFinal = true;
            }
            if (inFinal && z == cutDepth && next == tabDepth)
            {
                count++;
            }
            z = next;
        }
        return count;
    }

    private static double DistanceToPath(List<Pt> pts, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            Pt a = pts[i], b = pts[(i + 1) % pts.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            if (len2 == 0)
            {
                continue;
            }
            double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(a.X + t * dx - x, 2) + Math.Pow(a.Y + t * dy - y, 2)));
        }
        return best;
    }

    private static bool ProtectedPathIsSafe(List<Pt> path, Operation op, double diameter, List<TabGeometry.Interval> excluded)
    {
        var lengths = TabGeometry.CumulativeLengths(path);
        double total = lengths[^1];
        double center = Cam.ClosestDistance(path, op.Tabs[0]);
        var protectedPoints = new List<Pt>();
        for (int i = 0; i <= 100; i++)
        {
            double distance = (center - op.TabLength / 2 + op.TabLength * i / 100 + total) % total;
            protectedPoints.Add(PointAt(path, lengths, distance));
        }
        for (double distance = 0; distance < total; distance += 0.025)
        {
            if (excluded.Any(x => distance >= x.Start - 1e-6 && distance <= x.End + 1e-6))
            {
                continue;
            }
            Pt p = PointAt(path, lengths, distance);
            double nearest = double.MaxValue;
            for (int i = 0; i + 1 < protectedPoints.Count; i++)
            {
                Pt a = protectedPoints[i], b = protectedPoints[i + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y, length2 = dx * dx + dy * dy;
                if (length2 == 0)
                {
                    continue;
                }
                double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2, 0, 1);
                nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2)));
            }
            if (nearest < diameter / 2 - 0.0003)
            {
                return false;
            }
        }
        return true;
    }

    private static Pt PointAt(List<Pt> path, List<double> lengths, double distance)
    {
        for (int i = 0; i < path.Count; i++)
        {
            if (distance <= lengths[i + 1])
            {
                double t = (distance - lengths[i]) / (lengths[i + 1] - lengths[i]);
                Pt a = path[i], b = path[(i + 1) % path.Count];
                return new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
        }
        return path[0];
    }

    private static int CountFiveMillimeterBridgeMoves(string code)
    {
        double x = 0, y = 0, z = 0;
        bool inFinal = false;
        int count = 0;
        foreach (string line in code.Split('\n'))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || (words[0] != "G0" && words[0] != "G1"))
            {
                continue;
            }
            double nx = x, ny = y, nz = z;
            foreach (var word in words.Skip(1))
            {
                if (word.Length < 2 || !double.TryParse(word[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    continue;
                }
                if (word[0] == 'X')
                {
                    nx = value;
                }
                else if (word[0] == 'Y')
                {
                    ny = value;
                }
                else if (word[0] == 'Z')
                {
                    nz = value;
                }
            }
            if (nz == -18)
            {
                inFinal = true;
            }
            double distance = Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y));
            if (inFinal && words[0] == "G1" && z == -9 && nz == -9 && Math.Abs(distance - 5) < 0.002)
            {
                count++;
            }
            x = nx;
            y = ny;
            z = nz;
        }
        return count;
    }
}
