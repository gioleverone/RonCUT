using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RonCUT;

internal static class CutConnectionTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "cut-collegamento.json");
        const string legacy = """
            {
              "Port": "COM42",
              "Baud": 57600,
              "SelectedProfile": "Normal",
              "Profiles": [
                { "Name": "Slow", "Step": 0.2, "Feed": 65 },
                { "Name": "Normal", "Step": 2, "Feed": 150 },
                { "Name": "Fast", "Step": 3, "Feed": 400 }
              ]
            }
            """;
        File.WriteAllText(path, legacy);
        var migrated = CutSettings.Load(path);
        check(migrated.SelectedProfile == "Slow" && migrated.ActiveProfile.Step == 0.2
            && migrated.Slow.Feed == 65 && migrated.Fast.Step == 3 && migrated.Fast.Feed == 400,
            "CUT: il vecchio profilo Normal passa a Slow e i valori delle due modalità vengono conservati");
        File.WriteAllText(path, legacy.Replace("\"SelectedProfile\": \"Normal\"", "\"SelectedProfile\": \"Fast\""));
        check(CutSettings.Load(path).ActiveProfile.Step == 3, "CUT: una selezione Fast esistente viene conservata");
        migrated.Save(path);
        check(!File.ReadAllText(path).Contains("\"Profiles\"") && CutSettings.Load(path).Fast.Feed == 400,
            "CUT: il nuovo archivio contiene i due profili fissi");

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var serial = new ObservedTransport();
        var calls = new List<(string Port, int Baud)>();
        var window = new CutWindow(new[] { new CutPhase("G90\nG0 X7\nM30", "", 3) }, path,
            cncTransportFactory: (port, baud) =>
            {
                calls.Add((port, baud));
                return serial;
            })
        {
            ShowInTaskbar = false, Left = -5000, Top = -5000
        };
        try
        {
            window.Show();
            var connection = (TextBlock)window.FindName("ConnectionLabel");
            var start = (Button)window.FindName("StartButton");
            Program.PumpUntil(() => start.IsEnabled, 5000);
            check(calls.SequenceEqual(new[] { ("COM42", 57600) }) && connection.Text.StartsWith("CNC")
                && serial.Writes.Count(command => command == "$I") == 1
                && serial.Writes.All(command => command is "$I" or "?"),
                "CUT: all'apertura usa la porta configurata e invia solo identificazione e lettura dello stato");
            ((Button)window.FindName("SimulateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.PumpUntil(() => start.IsEnabled && connection.Text.StartsWith("SIMULAZIONE"), 5000);
            check(!serial.IsOpen && calls.Count == 1, "CUT: passando al simulatore si chiude la porta CNC senza riconnetterla");
        }
        finally
        {
            window.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class ObservedTransport : IGrblTransport
    {
        private readonly SimulatedGrblTransport _simulator = new();
        public ConcurrentQueue<string> Writes { get; } = new();
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

        public void Write(byte[] data)
        {
            Writes.Enqueue(Encoding.ASCII.GetString(data).Trim());
            _simulator.Write(data);
        }
    }
}
