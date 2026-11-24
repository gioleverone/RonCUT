using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RonCUT;

internal static class ToolChangeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string directory, Action<bool, string> check)
    {
        var job = TestDesign(directory, check);
        var phases = Cam.CompilePhases(job);
        check(phases.Count == 2 && phases[0].Gcode.TrimEnd().EndsWith("M5") && !phases[0].Gcode.Contains("M30") &&
            phases[1].Gcode.TrimEnd().EndsWith("M30"), "cambio utensile: ritiro e M5 tra le fasi, chiusura programma solo alla fine");
        string[] resumed = phases[1].Gcode.Split('\n', StringSplitOptions.TrimEntries);
        int raise = Array.IndexOf(resumed, "G0 Z1");
        int spindle = Array.IndexOf(resumed, "M3 S12000");
        int firstXY = Array.FindIndex(resumed, line => line.StartsWith("G0 X"));
        check(raise >= 0 && raise < spindle && spindle < firstXY && resumed[firstXY] != "G0 X0 Y0",
            "cambio utensile: la ripresa alza Z dalla posizione attuale prima di avviare e spostare la nuova fresa");

        job.Operations.RemoveAt(2);
        bool incompleteRejected = false;
        try
        {
            Cam.CompilePhases(job);
        }
        catch (InvalidOperationException)
        {
            incompleteRejected = true;
        }
        check(incompleteRejected, "cambio utensile: blocca il CUT se dopo la pausa manca una lavorazione");
        bool exportRejected = false;
        try
        {
            Cam.Generate(job);
        }
        catch (InvalidOperationException)
        {
            exportRejected = true;
        }
        check(exportRejected, "cambio utensile: nessuna esportazione di un file unico con movimenti oltre la pausa");

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            TestCut(directory, phases, check, interruption: "");
            TestCut(directory, phases, check, interruption: "STOP");
            TestCut(directory, phases, check, interruption: "reset");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static Job TestDesign(string directory, Action<bool, string> check)
    {
        const string svg = "<svg><rect x='1' y='1' width='4' height='4'/><rect x='7' y='1' width='4' height='4'/></svg>";
        var fast = new CuttingParameters { FeedXY = 3000, FeedZ = 1200, SpindleRpm = 12000 };
        var job = new Job
        {
            SvgText = svg,
            SvgName = "Cambio-utensile.svg",
            Contours = SvgImporter.Parse(svg, 1),
            Tool = new Tool { Name = "Sgrossatura", Diameter = 3 },
            Parameters = new CuttingParameters { FeedXY = 3000, FeedZ = 1200, SpindleRpm = 18000 },
            ZeroY = 6
        };
        job.Operations = job.Contours.Select(c => new Operation { ContourId = c.Id, Kind = "Taglio esterno", Depth = 0.25 }).ToList();
        var change = new ToolChange { MaterialName = job.Material.Name, Tool = new Tool { Name = "Finitura", Diameter = 1 }, Parameters = fast };
        string settingsPath = Path.Combine(directory, "cambio-generali.json");
        File.WriteAllText(settingsPath, "{\"SafeZ\":1,\"TabLength\":2}");
        var window = new MainWindow(settingsPath)
        {
            Width = 1450, Height = 950, Left = -5000, Top = -5000, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            Field(window, "_job").SetValue(window, job);
            Call(window, "SelectOnly", job.Operations[0]);
            Call(window, "RefreshOperations");
            Call(window, "WriteFields");
            var add = (Button)window.FindName("AddToolChangeButton");
            Call(window, "SaveToolChange", null, change);
            var list = (ListBox)window.FindName("OperationsList");
            check(Equals(add.Content, "Aggiungi pausa cambio utensile") && job.Operations[1].IsToolChange &&
                list.SelectedIndex == 1 && ((FrameworkElement)window.FindName("OperationDetails")).Visibility == Visibility.Collapsed,
                "cambio utensile: pulsante in DESIGN e riga dedicata dopo la selezione");
            var paths = Cam.Build(job);
            check(paths.Count == 2 && paths[0].ToolDiameter == 3 && paths[1].ToolDiameter == 1 &&
                Math.Abs(paths[0].Points.Min(p => p.X) + 0.5) < 0.002 && Math.Abs(paths[1].Points.Min(p => p.X) - 6.5) < 0.002,
                "cambio utensile: i contorni usano la compensazione della propria fresa");
            string project = Path.Combine(directory, "cambio-utensile.roncut");
            Field(window, "_filePath").SetValue(window, project);
            bool saved = (bool)Call(window, "Save", false)!;
            Call(window, "LoadProject", project);
            var reopened = (Job)Field(window, "_job").GetValue(window)!;
            check(saved && reopened.Version == 2 && reopened.Operations[1].NextTool?.Parameters.SpindleRpm == 12000 &&
                reopened.Operations[1].NextTool?.Tool.Diameter == 1,
                "cambio utensile: ordine, nuovo utensile e parametri sopravvivono a salvataggio e riapertura");
            return reopened;
        }
        finally
        {
            Field(window, "_dirty").SetValue(window, false);
            window.Close();
        }
    }

    private static void TestCut(string directory, IReadOnlyList<CutPhase> phases, Action<bool, string> check, string interruption)
    {
        using var transport = new RecordingTransport();
        using var handler = new NotificationHandler();
        using var http = new HttpClient(handler);
        var notifications = new CutNotificationService(http);
        var window = new CutWindow(phases, Path.Combine(directory, "cambio-cut.json"),
            new GeneralSettings { CommunicationsApiUrl = "https://example.invalid/notify" }, notifications)
        {
            Width = 1450, Height = 900, Left = -5000, Top = -5000, ShowInTaskbar = false
        };
        window.Show();
        WaitFor((Task)Call(window, "ConnectAsync", transport, false)!);
        var controller = (GrblController)Field(window, "_controller").GetValue(window)!;
        var start = (Button)window.FindName("StartButton");
        var pause = (FrameworkElement)window.FindName("ToolChangePanel");
        var resume = (Button)window.FindName("ResumeAfterToolChangeButton");
        var zeroZ = (Button)window.FindName("ToolChangeZeroZButton");
        Program.PumpUntil(() => start.IsEnabled, 5000);
        Click(start);
        Program.PumpUntil(() => pause.Visibility == Visibility.Visible && zeroZ.IsEnabled && handler.Messages.Count == 1, 6000);
        if (interruption != "")
        {
            if (interruption == "STOP")
            {
                Click((Button)window.FindName("StopButton"));
            }
            else
            {
                transport.ResetFromOutside();
            }
            Program.PumpUntil(() => pause.Visibility == Visibility.Collapsed && !controller.IsResetAwaitingConfirmation, 5000);
            WaitFor(notifications.PendingNotifications);
            check(!start.IsEnabled && !transport.Lines.Contains("M3 S12000") && handler.Messages.Count == 1,
                "cambio utensile: " + interruption + " in pausa annulla la sequenza e la notifica di fine");
            window.Close();
            return;
        }

        check(Equals(resume.Content, "Riprendi dopo la pausa") && !resume.IsEnabled && !start.IsEnabled &&
            !((Button)window.FindName("PauseButton")).IsEnabled && !controller.SpindleRunning && Math.Abs(controller.WorkPosition!.Z - 1) < 0.01 &&
            !transport.Lines.Contains("M3 S12000"), "cambio utensile: pannello dedicato dopo ritiro, M5 e Idle. Ripresa bloccata prima dello zero Z");
        long epoch = controller.CoordinateEpoch;
        var initial = controller.WorkPosition!;
        WaitFor((Task)Call(window, "Jog", "X+")!);
        WaitFor((Task)Call(window, "Jog", "Y-")!);
        var positioned = controller.WorkPosition!;
        check(positioned.X > initial.X && positioned.Y < initial.Y && controller.CoordinateEpoch == epoch &&
            !((FrameworkElement)window.FindName("ZeroControls")).IsEnabled, "cambio utensile: jog X/Y attivo e zero XY protetto durante la pausa");
        Click(resume);
        check(!transport.Lines.Contains("M3 S12000"), "cambio utensile: anche un click anticipato non avvia la fase successiva");
        Click(zeroZ);
        Program.PumpUntil(() => resume.IsEnabled, 5000);
        var zeroed = controller.WorkPosition!;
        check(zeroed.X == positioned.X && zeroed.Y == positioned.Y && Math.Abs(zeroed.Z) < 0.001 &&
            transport.Lines.Contains("G92Z0") && !transport.Lines.Contains("G92X0Y0"), "cambio utensile: azzera solo Z dopo gli spostamenti manuali");
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, "RonCUT-cambio-utensile.png")))
        {
            encoder.Save(file);
        }
        Click(resume);
        Program.PumpUntil(() => handler.Messages.Count == 2 && !controller.IsRunning, 7000);
        var messages = handler.Messages.ToArray();
        check(transport.Lines.Count(line => line == "M3 S12000") == 1 && messages[0].Contains("CNC Start") && messages[1].Contains("CNC End") && !start.IsEnabled,
            "cambio utensile: ripresa una sola volta, notifiche soltanto ai due estremi e protezione da riavvio con la fresa finale");
        window.Close();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void WaitFor(Task task)
    {
        Program.PumpUntil(() => task.IsCompleted, 6000);
        task.GetAwaiter().GetResult();
    }

    private static FieldInfo Field(Window window, string name) => window.GetType().GetField(name, Private)!;

    private static object? Call(Window window, string name, params object?[] args)
        => window.GetType().GetMethod(name, Private)!.Invoke(window, args);

    private sealed class RecordingTransport : IGrblTransport
    {
        private readonly SimulatedGrblTransport _simulator = new();
        public ConcurrentQueue<string> Lines { get; } = new();
        public bool IsOpen => _simulator.IsOpen;
        public event Action<string>? LineReceived
        {
            add => _simulator.LineReceived += value;
            remove => _simulator.LineReceived -= value;
        }
        public event Action<Exception>? Failed
        {
            add => _simulator.Failed += value;
            remove => _simulator.Failed -= value;
        }
        public void Open() => _simulator.Open();
        public void Dispose() => _simulator.Dispose();
        public void ResetFromOutside() => _simulator.Write(new byte[] { 0x18 });
        public void Write(byte[] data)
        {
            if (data.Length > 1)
            {
                Lines.Enqueue(Encoding.ASCII.GetString(data).Trim());
            }
            _simulator.Write(data);
        }
    }

    private sealed class NotificationHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Messages.Enqueue(Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
