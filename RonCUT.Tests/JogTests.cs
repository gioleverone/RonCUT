using System.Collections.Concurrent;
using System.IO;
using System.Text;
using RonCUT;

internal static class JogTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using (var transport = new JogTransport())
        using (var controller = new GrblController(transport))
        {
            await controller.ConnectAsync();
            Task jog = controller.JogContinuousAsync('X', 1, 600);
            await WaitUntil(() => transport.Commands.Count(line => line.StartsWith("$J=")) >= 3);
            check(controller.IsJogging && !controller.IsReady, "jog continuo: invii ripetuti senza dipendere dalla ripetizione del tastierino");
            bool blocked = false;
            try
            {
                _ = controller.StartAsync("G21\nG90\n");
            }
            catch (InvalidOperationException)
            {
                blocked = true;
            }
            check(blocked && !transport.Commands.Contains("G21"), "jog continuo: START resta bloccato durante il movimento manuale");
            blocked = false;
            try
            {
                await controller.JogAsync('Y', -1, 1, 100);
            }
            catch (InvalidOperationException)
            {
                blocked = true;
            }
            check(blocked, "jog continuo: un secondo comando di direzione non viene accodato");
            Task cancelled = controller.CancelJogAsync();
            await cancelled;
            await jog;
            check(transport.Realtime.Count(value => value == 0x85) == 1 && !transport.Realtime.Contains((byte)0x18) && transport.Commands.Contains("G4P0") && controller.IsReady,
                "STOP manuale: jog cancel, barriera G4P0 e nuovo Idle senza soft reset");
            string[] events = transport.Events.ToArray();
            check(Array.IndexOf(events, "RT:85") < Array.IndexOf(events, "CMD:G4P0"), "STOP manuale: il cancel viene scritto prima della barriera di sincronizzazione");
            int sent = transport.Commands.Count;
            await Task.Delay(180);
            check(transport.Commands.Count == sent, "STOP manuale: nessun altro movimento viene inviato dopo l'annullamento");
            Task next = controller.JogContinuousAsync('Y', -1, 300);
            await WaitUntil(() => transport.Commands.Any(line => line.StartsWith("$J=G91G21Y-")));
            await controller.CancelJogAsync();
            await next;
            check(controller.IsReady, "jog continuo: una nuova pressione riparte soltanto dopo la conferma dell'arresto");
            Task unattended = controller.JogContinuousAsync('X', 1, 200);
            await unattended.WaitAsync(TimeSpan.FromSeconds(3));
            check(controller.IsReady && !controller.IsJogging, "jog continuo: se l'interfaccia smette di confermare la pressione il movimento viene annullato");
            Task renewed = controller.JogContinuousAsync('X', 1, 200);
            for (int i = 0; i < 9; i++)
            {
                await Task.Delay(100);
                controller.RenewJogHold();
            }
            check(!renewed.IsCompleted && controller.IsJogging, "jog continuo: la conferma periodica della pressione mantiene il movimento");
            await controller.CancelJogAsync();
            await renewed;
            Task program = controller.StartAsync("G21\nG90\nG1 X10 F100\n");
            blocked = false;
            int cancelCount = transport.Realtime.Count(value => value == 0x85);
            try
            {
                await controller.CancelJogAsync();
            }
            catch (InvalidOperationException)
            {
                blocked = true;
            }
            check(blocked && transport.Realtime.Count(value => value == 0x85) == cancelCount, "STOP manuale: non invia comandi durante il programma CUT");
            controller.Stop();
            await program;
        }

        using (var transport = new JogTransport())
        using (var controller = new GrblController(transport))
        {
            await controller.ConnectAsync();
            transport.AckJogs = false;
            transport.DelayJogParsing = true;
            Task jog = controller.JogContinuousAsync('Z', 1, 100);
            await WaitUntil(() => transport.Commands.Any(line => line.StartsWith("$J=")));
            Task cancelled = controller.CancelJogAsync();
            check(transport.Realtime.Contains((byte)0x85) && !transport.Commands.Contains("G4P0"), "STOP manuale: annulla subito anche se manca ancora l'ACK del jog");
            transport.AckBarrier = false;
            transport.ReplyJogAck();
            await WaitUntil(() => transport.Commands.Contains("G4P0"));
            check(!controller.IsReady && !cancelled.IsCompleted, "STOP manuale: un ACK tardivo del jog non viene scambiato per conferma della barriera");
            bool blocked = false;
            try
            {
                await controller.SetZeroAsync(true);
            }
            catch (InvalidOperationException)
            {
                blocked = true;
            }
            check(blocked && !transport.Commands.Any(line => line.StartsWith("G92")), "STOP manuale: azzeramenti bloccati fino alla risincronizzazione completa");
            transport.Reply("ok");
            await cancelled.WaitAsync(TimeSpan.FromSeconds(3));
            await jog;
            check(controller.IsReady && transport.Realtime.Count(value => value == 0x85) == 2,
                "STOP manuale: cancel ribadito dopo un jog interpretato in ritardo, poi barriera e nuova posizione Idle");
        }

        foreach (string missing in new[] { "ACK jog", "ACK barriera", "stato Idle" })
        {
            using var transport = new JogTransport();
            using var controller = new GrblController(transport);
            await controller.ConnectAsync();
            transport.AckJogs = missing != "ACK jog";
            transport.AckBarrier = missing != "ACK barriera";
            transport.CancelStops = missing != "stato Idle";
            Task jog = controller.JogContinuousAsync('X', 1, 100);
            await WaitUntil(() => transport.Commands.Any(line => line.StartsWith("$J=")));
            Task cancelled = controller.CancelJogAsync();
            bool failed = false;
            try
            {
                await cancelled.WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (TimeoutException)
            {
                failed = true;
            }
            catch (IOException)
            {
                failed = true;
            }
            try
            {
                await jog.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception) when (failed)
            {
            }
            check(failed && !controller.IsReady && !transport.Realtime.Contains((byte)0x18), "STOP manuale: manca " + missing + ", avvio bloccato senza inviare reset automatici");
        }

        foreach (string failure in new[] { "disconnessione", "riavvio GRBL" })
        {
            using var transport = new JogTransport();
            using var controller = new GrblController(transport);
            await controller.ConnectAsync();
            Task jog = controller.JogContinuousAsync('X', 1, 100);
            await WaitUntil(() => transport.Commands.Any(line => line.StartsWith("$J=")));
            if (failure == "disconnessione")
            {
                transport.LoseConnection();
            }
            else
            {
                transport.Reply("Grbl 1.1h ['$' for help]");
            }
            bool failed = false;
            try
            {
                await jog.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                failed = true;
            }
            int sent = transport.Commands.Count;
            await Task.Delay(100);
            check(failed && !controller.IsReady && transport.Commands.Count == sent, "jog continuo: " + failure + " interrompe gli invii e richiede verifica della posizione");
        }

        using (var transport = new JogTransport())
        using (var controller = new GrblController(transport))
        {
            await controller.ConnectAsync();
            Task jog = controller.JogContinuousAsync('X', 1, 100);
            await WaitUntil(() => transport.Commands.Any(line => line.StartsWith("$J=")));
            transport.ThrowOnCancel = true;
            bool failed = false;
            try
            {
                await controller.CancelJogAsync();
            }
            catch (IOException)
            {
                failed = true;
            }
            try
            {
                await jog.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (IOException)
            {
                failed = true;
            }
            int sent = transport.Commands.Count;
            await Task.Delay(150);
            check(failed && !controller.IsReady && transport.Commands.Count == sent, "STOP manuale: errore di scrittura del cancel arresta gli invii e mantiene START bloccato");
        }

        using (var simulator = new SimulatedGrblTransport())
        using (var controller = new GrblController(simulator))
        {
            await controller.ConnectAsync();
            Task program = controller.StartAsync("M3 S10000\n");
            await WaitUntil(() =>
            {
                controller.QueryStatus();
                return program.IsCompleted;
            });
            await program;
            Task jog = controller.JogContinuousAsync('X', 1, 600);
            await WaitUntil(() =>
            {
                controller.QueryStatus();
                return controller.WorkPosition!.X > 0.4;
            });
            await controller.CancelJogAsync();
            await jog;
            double position = controller.WorkPosition!.X;
            await Task.Delay(220);
            controller.QueryStatus();
            check(controller.IsReady && Math.Abs(controller.WorkPosition!.X - position) < 0.001 && controller.SpindleRunning,
                "simulatore: STOP manuale svuota la coda, conserva posizione e non spegne il mandrino");
            await controller.JogAsync('Y', -1, 0.25, 600);
            check(Math.Abs(controller.WorkPosition!.Y + 0.25) < 0.002, "simulatore: il clic breve conserva il passo esatto del profilo");
            controller.Stop();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condizione di prova jog non raggiunta.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class JogTransport : IGrblTransport
    {
        public event Action<string>? LineReceived;
        public event Action<Exception>? Failed;
        public bool IsOpen { get; private set; }
        public bool AckJogs { get; set; } = true;
        public bool AckBarrier { get; set; } = true;
        public bool CancelStops { get; set; } = true;
        public bool DelayJogParsing { get; set; }
        public bool ThrowOnCancel { get; set; }
        public ConcurrentQueue<string> Commands { get; } = new();
        public ConcurrentQueue<byte> Realtime { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        private volatile string _state = "Idle";

        public void Open()
        {
            IsOpen = true;
            Reply("Grbl 1.1h ['$' for help]");
        }

        public void Write(byte[] data)
        {
            if (data.Length == 1)
            {
                Realtime.Enqueue(data[0]);
                Events.Enqueue("RT:" + data[0].ToString("X2"));
                if (data[0] == 0x85 && ThrowOnCancel)
                {
                    throw new IOException("Scrittura del cancel fallita nel test");
                }
                if (data[0] == '?')
                {
                    Reply("<" + _state + "|WPos:0,0,0|Ov:100,100,100>");
                }
                else if (data[0] == 0x85 && CancelStops && _state == "Jog")
                {
                    _state = "Idle";
                }
                else if (data[0] == 0x18)
                {
                    _state = "Idle";
                    Reply("Grbl 1.1h ['$' for help]");
                }
                return;
            }
            string command = Encoding.ASCII.GetString(data).Trim();
            Commands.Enqueue(command);
            Events.Enqueue("CMD:" + command);
            if (command.StartsWith("$J="))
            {
                if (!DelayJogParsing)
                {
                    _state = "Jog";
                }
                if (AckJogs)
                {
                    Reply("ok");
                }
            }
            else if (command == "G4P0")
            {
                if (AckBarrier)
                {
                    Reply("ok");
                }
            }
            else
            {
                if (command == "$I")
                {
                    Reply("[VER:1.1h.test]");
                }
                Reply("ok");
            }
        }

        public void Reply(string line) => LineReceived?.Invoke(line);

        public void ReplyJogAck()
        {
            _state = "Jog";
            Reply("ok");
        }

        public void LoseConnection()
        {
            IsOpen = false;
            Failed?.Invoke(new IOException("USB scollegata nel test"));
        }

        public void Dispose() => IsOpen = false;
    }
}
