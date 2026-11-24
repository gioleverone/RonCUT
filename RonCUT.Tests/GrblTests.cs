using System.Text;
using System.IO;
using RonCUT;

internal static class GrblTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var simulation = new SimulatedGrblTransport();
        var c = new GrblController(simulation);
        await c.ConnectAsync();
        c.QueryStatus();
        check(c.IsReady && c.Version.Contains("1.1"), "GRBL: riconoscimento 1.1 e stato pronto nel simulatore");
        await c.JogAsync('X', 1, 1, 6000);
        await WaitUntil(() => { c.QueryStatus(); return c.IsReady; }, 3000);
        check(c.WorkPosition != null && Math.Abs(c.WorkPosition.X - 1) < 0.02, "simulatore: movimento manuale e posizione pezzo");
        await c.SetZeroAsync(true);
        await WaitUntil(() => { c.QueryStatus(); return c.IsReady && Math.Abs(c.WorkPosition?.X ?? 1) < 0.001; }, 3000);
        check(Math.Abs(c.WorkPosition!.X) < 0.001, "simulatore: azzeramento XY");

        Task run = c.StartAsync("G21\nG90\nM3 S10000\nG1 X25 F600\nM5\nM30\n");
        await WaitUntil(() => { c.QueryStatus(); return c.State == "Run" && c.SpindleRunning && c.WorkPosition!.X > 0.1; }, 3000);
        bool blocked = false;
        try
        {
            await c.JogAsync('Y', 1, 1, 100);
        }
        catch (InvalidOperationException)
        {
            blocked = true;
        }
        check(blocked, "GRBL: movimenti manuali vietati durante il programma");
        c.Pause();
        await WaitUntil(() => { c.QueryStatus(); return c.IsPaused; }, 2000);
        double pausedX = c.WorkPosition!.X;
        await Task.Delay(250);
        c.QueryStatus();
        check(c.SpindleRunning && Math.Abs(c.WorkPosition!.X - pausedX) < 0.001, "pausa: posizione ferma e mandrino attivo in simulazione");
        blocked = false;
        try
        {
            await c.JogAsync('X', 1, 1, 100);
        }
        catch (InvalidOperationException)
        {
            blocked = true;
        }
        check(blocked, "pausa: movimento manuale vietato");
        c.SetOverrides(130, 120);
        await WaitUntil(() => { c.QueryStatus(); return c.FeedOverride == 130 && c.SpindleOverride == 120; }, 5000);
        check(c.FeedOverride == 130 && c.SpindleOverride == 120, "override: conferma separata di feed e mandrino");
        c.Resume();
        await WaitUntil(() => { c.QueryStatus(); return c.State == "Run" && c.WorkPosition!.X > pausedX + 0.1; }, 3000);
        check(c.IsRunning, "ripresa: simulazione prosegue dalla pausa");
        c.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        c.QueryStatus();
        double stoppedX = c.WorkPosition!.X;
        await Task.Delay(200);
        c.QueryStatus();
        check(!c.IsRunning && !c.SpindleRunning && string.IsNullOrEmpty(c.LastError) && Math.Abs(c.WorkPosition!.X - stoppedX) < 0.001, "stop: svuota la simulazione e non riparte");
        Task completed = c.StartAsync("G21\nG90\nM3 S10000\nG1 X2 F6000\nM5\nM30\n");
        await WaitUntil(() => { c.QueryStatus(); return completed.IsCompleted; }, 5000);
        await completed;
        c.QueryStatus();
        check(c.State == "Idle" && !c.SpindleRunning && Math.Abs(c.WorkPosition!.X - 2) < 0.02 && c.FeedOverride == 100 && c.SpindleOverride == 100, "simulatore: esecuzione completa, arrivo, spegnimento e fine Idle");
        c.Dispose();

        var fake = new ControlledTransport();
        var real = new GrblController(fake);
        Task connecting = real.ConnectAsync();
        real.QueryStatus();
        check(!real.IsReady, "seriale: i comandi restano bloccati fino al riconoscimento completo");
        await connecting;
        real.QueryStatus();
        fake.AutoAck = false;
        Task first = real.JogAsync('X', 1, 1, 200);
        blocked = false;
        try
        {
            await real.JogAsync('Y', 1, 1, 200);
        }
        catch (InvalidOperationException)
        {
            blocked = true;
        }
        check(blocked && fake.Commands.Count(x => x.StartsWith("$J=")) == 1, "seriale: comandi manuali concorrenti non vengono accodati");
        fake.Reply("ok");
        await first;
        real.QueryStatus();
        fake.AutoAck = false;
        Task stream = real.StartAsync("G21\nG90\nG1 X10 F100\nM5\n");
        await WaitUntil(() => fake.Commands.Contains("G21"), 2000);
        check(!fake.Commands.Contains("G90"), "seriale: invio aspetta conferma prima della riga successiva");
        real.Pause();
        fake.Reply("ok");
        fake.Reply("<Hold:0|WPos:0,0,0|Ov:100,100,100|A:S>");
        await Task.Delay(150);
        check(!fake.Commands.Contains("G90"), "seriale: nessuna nuova riga inviata durante pausa");
        real.Stop();
        await stream.WaitAsync(TimeSpan.FromSeconds(3));
        check(fake.Realtime.Contains(0x18) && !fake.Commands.Contains("G90"), "seriale: STOP annulla invio e manda Ctrl-X");
        real.Dispose();

        var faultTransport = new ControlledTransport();
        var faultController = new GrblController(faultTransport);
        await faultController.ConnectAsync();
        faultController.QueryStatus();
        faultTransport.AutoAck = false;
        Task failingRun = faultController.StartAsync("G21\nG90\nG1 X10 F100\n");
        await WaitUntil(() => faultTransport.Commands.Contains("G21"), 1000);
        faultTransport.Reply("error:33");
        await failingRun.WaitAsync(TimeSpan.FromSeconds(3));
        check(faultTransport.Realtime.Contains(0x18) && !faultTransport.Commands.Contains("G90") && faultController.LastError.Contains("error:33"), "seriale: errore GRBL interrompe invio e richiede reset");
        faultController.Dispose();

        var lostTransport = new ControlledTransport();
        var lostController = new GrblController(lostTransport);
        await lostController.ConnectAsync();
        lostController.QueryStatus();
        lostTransport.AutoAck = false;
        Task lostRun = lostController.StartAsync("G21\nG90\n");
        await WaitUntil(() => lostTransport.Commands.Contains("G21"), 1000);
        lostTransport.LoseConnection();
        await lostRun.WaitAsync(TimeSpan.FromSeconds(3));
        check(!lostController.IsConnected && !lostController.IsReady && !lostTransport.Commands.Contains("G90") && lostController.LastError.Contains("Connessione persa"), "seriale: perdita comunicazione blocca l'invio e segnala il problema");
        lostController.Dispose();

        var slowTransport = new ControlledTransport();
        var slowController = new GrblController(slowTransport);
        await slowController.ConnectAsync();
        slowController.QueryStatus();
        slowTransport.AutoAck = false;
        Task slowRun = slowController.StartAsync("G21\nG90\n");
        await WaitUntil(() => slowTransport.Commands.Contains("G21"), 1000);
        slowTransport.StatusState = "Run";
        DateTime end = DateTime.UtcNow.AddSeconds(5.7);
        while (DateTime.UtcNow < end)
        {
            slowController.QueryStatus();
            await Task.Delay(100);
        }
        check(slowController.IsRunning && !slowTransport.Realtime.Contains(0x18), "seriale: un movimento lungo non provoca reset se GRBL continua a rispondere");
        slowController.Stop();
        await slowRun.WaitAsync(TimeSpan.FromSeconds(3));
        slowController.Dispose();

        var staleTransport = new ControlledTransport();
        var staleController = new GrblController(staleTransport);
        await staleController.ConnectAsync();
        staleController.QueryStatus();
        staleTransport.AutoAck = false;
        Task staleRun = staleController.StartAsync("G21\nG90\n");
        await WaitUntil(() => staleTransport.Commands.Contains("G21"), 1000);
        await Task.Delay(5200);
        staleTransport.Reply("ok");
        await staleRun.WaitAsync(TimeSpan.FromSeconds(3));
        check(!staleTransport.Commands.Contains("G90") && staleTransport.Realtime.Contains(0x18), "seriale: senza aggiornamenti di stato l'invio si interrompe");
        staleController.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condizione GRBL non raggiunta entro " + timeoutMs + " ms.");
            }
            await Task.Delay(50);
        }
    }

    internal sealed class ControlledTransport : IGrblTransport
    {
        public event Action<string>? LineReceived;
        public event Action<Exception>? Failed;
        public bool IsOpen { get; private set; }
        public bool AutoAck { get; set; } = true;
        public string StatusState { get; set; } = "Idle";
        public List<string> Commands { get; } = new List<string>();
        public List<byte> Realtime { get; } = new List<byte>();

        public void Open()
        {
            IsOpen = true;
            Reply("Grbl 1.1h ['$' for help]");
        }

        public void Write(byte[] bytes)
        {
            if (bytes.Length == 1)
            {
                Realtime.Add(bytes[0]);
                if (bytes[0] == '?')
                {
                    Reply("<" + StatusState + "|WPos:0,0,0|Ov:100,100,100>");
                }
                else if (bytes[0] == 0x18)
                {
                    Reply("Grbl 1.1h ['$' for help]");
                }
                return;
            }
            string command = Encoding.ASCII.GetString(bytes).Trim();
            Commands.Add(command);
            if (command == "$I")
            {
                Reply("[VER:1.1h.test]");
            }
            if (AutoAck)
            {
                Reply("ok");
            }
        }

        public void Reply(string line) => LineReceived?.Invoke(line);
        public void LoseConnection() => Failed?.Invoke(new IOException("USB scollegata"));
        public void Dispose() => IsOpen = false;
    }
}
