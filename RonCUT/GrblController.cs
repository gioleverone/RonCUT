using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RonCUT;

public sealed record MachinePosition(double X, double Y, double Z);

public sealed partial class GrblController : IDisposable
{
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
    private readonly IGrblTransport _transport;
    private TaskCompletionSource<string>? _pending;
    private CancellationTokenSource? _runCancellation;
    private DateTime _statusTime = DateTime.MinValue;
    private MachinePosition? _offset;
    private bool _identified;
    private bool _handshakeComplete;
    private bool _holdRequested;
    private bool _disposed;
    private bool _desynced;
    private bool _connectionFault;
    private bool _manualPending;
    private long _coordinateEpoch;
    private bool _resetRequested;
    private DateTime _resetSentAt = DateTime.MinValue;
    private int? _requestedFeed;
    private int? _requestedSpindle;
    private bool _feedAwaitingReport;
    private bool _spindleAwaitingReport;
    private readonly Stopwatch _clock = new Stopwatch();

    public event Action? Changed;
    public event Action<string>? Log;
    public string State { get; private set; } = "Disconnessa";
    public string Version { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public MachinePosition? WorkPosition { get; private set; }
    public int FeedOverride { get; private set; } = 100;
    public int SpindleOverride { get; private set; } = 100;
    public bool SpindleRunning { get; private set; }
    public int LinesSent { get; private set; }
    public int TotalLines { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsConnected => !_disposed && !_connectionFault && _transport.IsOpen;
    public long CoordinateEpoch => Interlocked.Read(ref _coordinateEpoch);
    public bool IsReady => IsConnected && !_desynced && !_manualPending && _identified && _handshakeComplete && WorkPosition != null && State == "Idle" && DateTime.UtcNow - _statusTime < TimeSpan.FromSeconds(3) && !IsRunning;
    public bool IsPaused => State.Equals("Hold:0", StringComparison.OrdinalIgnoreCase);
    public bool IsResetPending => IsConnected && (_resetRequested || _desynced || State == "Reset");
    public bool IsResetAwaitingConfirmation => IsResetPending && DateTime.UtcNow - _resetSentAt < TimeSpan.FromSeconds(5);
    public TimeSpan Elapsed => _clock.Elapsed;

    public GrblController(IGrblTransport transport)
    {
        _transport = transport;
        _transport.LineReceived += OnLine;
        _transport.Failed += OnFailure;
    }

    public async Task ConnectAsync()
    {
        _transport.Open();
        State = "Connessione";
        Changed?.Invoke();
        await Task.Delay(1500);
        if (!IsConnected)
        {
            throw new IOException("Connessione seriale non disponibile.");
        }
        await SendCommandAsync("$I", CancellationToken.None, false);
        if (!_identified)
        {
            throw new InvalidOperationException("Non è stata riconosciuta una scheda GRBL 1.1. Controlla la porta e la velocità.");
        }
        _handshakeComplete = true;
        QueryStatus();
    }

    public void QueryStatus()
    {
        if (IsConnected)
        {
            WriteRealtime((byte)'?');
        }
    }

    public Task JogAsync(char axis, int direction, double step, double feed) => BeginJog(axis, direction, step, feed, false);

    public Task JogContinuousAsync(char axis, int direction, double feed) => BeginJog(axis, direction, Math.Max(0.001, feed / 1000), feed, true);

    public async Task SetZeroAsync(bool xy)
    {
        await RunManualAsync(xy ? "G92X0Y0" : "G92Z0");
        lock (_gate)
        {
            _offset = null;
            WorkPosition = null;
            _statusTime = DateTime.MinValue;
        }
        Changed?.Invoke();
    }

    private async Task RunManualAsync(string command)
    {
        lock (_gate)
        {
            EnsureIdle();
            _manualPending = true;
            _statusTime = DateTime.MinValue;
        }
        Changed?.Invoke();
        try
        {
            await SendCommandAsync(command, CancellationToken.None, true);
        }
        finally
        {
            lock (_gate)
            {
                _manualPending = false;
            }
            Changed?.Invoke();
        }
    }

    public Task<bool> StartAsync(string gcode, long? expectedCoordinateEpoch = null)
    {
        var lines = PrepareLines(gcode);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("Il programma è vuoto.");
        }
        lock (_gate)
        {
            EnsureIdle();
            if (expectedCoordinateEpoch.HasValue && (expectedCoordinateEpoch.Value != CoordinateEpoch || SpindleRunning))
            {
                throw new InvalidOperationException("Ripresa bloccata. Verifica il riferimento pezzo e lo stato del mandrino.");
            }
            if (IsRunning)
            {
                throw new InvalidOperationException("Una lavorazione è già in corso.");
            }
            _transport.Write(new byte[] { 0x90 });
            _transport.Write(new byte[] { 0x99 });
            _requestedFeed = null;
            _requestedSpindle = null;
            _feedAwaitingReport = false;
            _spindleAwaitingReport = false;
            IsRunning = true;
            _holdRequested = false;
            _runCancellation = new CancellationTokenSource();
            LinesSent = 0;
            TotalLines = lines.Count;
            LastError = "";
            _clock.Restart();
        }
        Changed?.Invoke();
        return StreamAsync(lines, _runCancellation.Token);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning || _holdRequested || IsPaused)
            {
                throw new InvalidOperationException("La lavorazione non può essere messa in pausa in questo momento.");
            }
            _holdRequested = true;
            _transport.Write(new byte[] { (byte)'!' });
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsRunning || !IsPaused)
            {
                throw new InvalidOperationException("Attendi che GRBL confermi la pausa prima di riprendere.");
            }
            _resumeRequested = true;
            _transport.Write(new byte[] { (byte)'~' });
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _coordinateEpoch);
            _desynced = true;
            _resetRequested = true;
            _resetSentAt = DateTime.UtcNow;
            _statusTime = DateTime.MinValue;
            _jogResetRequested = true;
            _jogCancellation?.Cancel();
            _runCancellation?.Cancel();
            _pending?.TrySetCanceled();
            _pending = null;
            _holdRequested = false;
            _resumeRequested = false;
            _offset = null;
            WorkPosition = null;
            _feedAwaitingReport = false;
            _spindleAwaitingReport = false;
            _requestedFeed = null;
            _requestedSpindle = null;
            if (IsConnected)
            {
                _transport.Write(new byte[] { 0x18 });
            }
            State = "Reset";
        }
        Changed?.Invoke();
    }

    public async Task UnlockAsync()
    {
        lock (_gate)
        {
            if (!IsConnected || !_identified || !_handshakeComplete || _desynced || IsRunning || _manualPending || !State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Lo sblocco è disponibile solo quando GRBL segnala un allarme.");
            }
            _manualPending = true;
        }
        try
        {
            await SendCommandAsync("$X", CancellationToken.None, true);
        }
        finally
        {
            lock (_gate)
            {
                _manualPending = false;
            }
            Changed?.Invoke();
        }
    }

    public void SetOverrides(int? feed, int? spindle)
    {
        lock (_gate)
        {
            if (!IsRunning || !IsConnected)
            {
                throw new InvalidOperationException("Le regolazioni sono disponibili durante una lavorazione.");
            }
            if (feed.HasValue)
            {
                _requestedFeed = Math.Clamp(feed.Value, 10, 200);
            }
            if (spindle.HasValue)
            {
                _requestedSpindle = Math.Clamp(spindle.Value, 10, 200);
            }
            SendPendingOverrides();
        }
    }

    private void SendPendingOverrides()
    {
        if (_requestedFeed.HasValue && !_feedAwaitingReport && _requestedFeed.Value != FeedOverride)
        {
            _feedAwaitingReport = true;
            SendOverrideSteps(FeedOverride, _requestedFeed.Value, 0x91, 0x92, 0x93, 0x94);
        }
        if (_requestedSpindle.HasValue && !_spindleAwaitingReport && _requestedSpindle.Value != SpindleOverride)
        {
            _spindleAwaitingReport = true;
            SendOverrideSteps(SpindleOverride, _requestedSpindle.Value, 0x9A, 0x9B, 0x9C, 0x9D);
        }
    }

    private void SendOverrideSteps(int current, int target, byte coarseUp, byte coarseDown, byte fineUp, byte fineDown)
    {
        int difference = target - current;
        byte coarse = difference > 0 ? coarseUp : coarseDown;
        byte fine = difference > 0 ? fineUp : fineDown;
        byte command = Math.Abs(difference) >= 10 ? coarse : fine;
        _transport.Write(new byte[] { command });
    }

    private async Task<bool> StreamAsync(List<string> lines, CancellationToken token)
    {
        try
        {
            foreach (var line in lines)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow - _statusTime > TimeSpan.FromSeconds(5))
                    {
                        throw new TimeoutException("Nessuna risposta di stato dalla CNC durante l'invio.");
                    }
                    if (State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("GRBL ha segnalato un allarme.");
                    }
                    lock (_gate)
                    {
                        if (!_holdRequested)
                        {
                            break;
                        }
                    }
                    await Task.Delay(40, token);
                }
                await SendCommandAsync(line, token, false);
                LinesSent++;
            }
            DateTime sentAll = DateTime.UtcNow;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsConnected)
                {
                    throw new IOException("Connessione persa durante la lavorazione.");
                }
                if (State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("GRBL ha segnalato un allarme.");
                }
                if (State == "Idle" && _statusTime > sentAll)
                {
                    break;
                }
                if (DateTime.UtcNow - sentAll > TimeSpan.FromSeconds(5) && DateTime.UtcNow - _statusTime > TimeSpan.FromSeconds(5))
                {
                    throw new TimeoutException("Nessuna risposta di stato dalla CNC.");
                }
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke("Lavorazione interrotta. Verifica posizione e stato della macchina.");
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log?.Invoke("ERRORE: " + ex.Message);
            try
            {
                Stop();
            }
            catch
            {
                Log?.Invoke("Comunicazione interrotta: verifica immediatamente la macchina.");
            }
            return false;
        }
        finally
        {
            lock (_gate)
            {
                IsRunning = false;
                _clock.Stop();
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
            Changed?.Invoke();
        }
    }

    private async Task SendCommandAsync(string command, CancellationToken token, bool invalidateStatus, CancellationToken writeToken = default, bool resetOnTimeout = true)
    {
        await _commandGate.WaitAsync(token);
        try
        {
            TaskCompletionSource<string>? completion = null;
            while (completion == null)
            {
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    writeToken.ThrowIfCancellationRequested();
                    if (!IsConnected || _desynced)
                    {
                        throw new IOException("Porta seriale non disponibile o comunicazione da riavviare.");
                    }
                    if (!IsRunning || !_holdRequested)
                    {
                        if (invalidateStatus)
                        {
                            _statusTime = DateTime.MinValue;
                        }
                        completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _pending = completion;
                        _transport.Write(Encoding.ASCII.GetBytes(command + "\n"));
                    }
                }
                if (completion == null)
                {
                    await Task.Delay(40, token);
                }
            }
            string response;
            try
            {
                DateTime sent = DateTime.UtcNow;
                while (!completion.Task.IsCompleted)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.WhenAny(completion.Task, Task.Delay(250, token));
                    if (DateTime.UtcNow - sent > TimeSpan.FromSeconds(5) &&
                        (!IsRunning || State == "Idle" || DateTime.UtcNow - _statusTime > TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException();
                    }
                }
                response = await completion.Task;
            }
            catch (TimeoutException)
            {
                lock (_gate)
                {
                    _desynced = true;
                    _statusTime = DateTime.MinValue;
                    if (resetOnTimeout)
                    {
                        _resetRequested = true;
                        _resetSentAt = DateTime.UtcNow;
                        if (IsConnected)
                        {
                            _transport.Write(new byte[] { 0x18 });
                        }
                    }
                }
                throw new TimeoutException("GRBL non ha confermato il comando: riconnetti e verifica lo stato della macchina.");
            }
            if (!response.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GRBL: " + response + " (" + command + ")");
            }
        }
        finally
        {
            lock (_gate)
            {
                _pending = null;
            }
            _commandGate.Release();
        }
    }

    private static List<string> PrepareLines(string gcode)
    {
        var result = new List<string>();
        foreach (string raw in gcode.Split('\n'))
        {
            string line = Regex.Replace(raw, @"\([^)]*\)", "").Trim();
            if (line.Length == 0 || line.StartsWith(";"))
            {
                continue;
            }
            if (line.Length > 100 || line.Any(c => c > 127 || c < 32))
            {
                throw new InvalidOperationException("Riga G-code troppo lunga o non valida.");
            }
            result.Add(line);
        }
        return result;
    }

    private void EnsureIdle()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException("La CNC deve essere connessa, riconosciuta e ferma prima di questo comando.");
        }
    }

    private void WriteRealtime(byte command)
    {
        lock (_gate)
        {
            _transport.Write(new byte[] { command });
        }
    }

    private void OnLine(string line)
    {
        lock (_gate)
        {
            if (line.StartsWith("Grbl 1.1", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[VER:1.1", StringComparison.OrdinalIgnoreCase))
            {
                _identified = true;
                Version = line;
            }
            if (line.StartsWith("Grbl ", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _coordinateEpoch);
                _desynced = !line.StartsWith("Grbl 1.1", StringComparison.OrdinalIgnoreCase);
                _offset = null;
                WorkPosition = null;
                _statusTime = DateTime.MinValue;
                if (IsRunning && !_resetRequested)
                {
                    _runCancellation?.Cancel();
                    LastError = "GRBL si è riavviato durante la lavorazione.";
                }
                if (_jogCancellation != null && !_resetRequested)
                {
                    _jogFailure = new InvalidOperationException("GRBL si è riavviato durante il movimento manuale: verifica la posizione e riconnetti.");
                    _jogResetRequested = true;
                    _desynced = true;
                    _jogCancellation.Cancel();
                    _pending?.TrySetException(_jogFailure);
                }
                _resetRequested = false;
            }
            if (line.StartsWith("<") && line.EndsWith(">"))
            {
                ParseStatus(line);
            }
            else if (line.Equals("ok", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                _pending?.TrySetResult(line);
            }
            else if (line.StartsWith("ALARM", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _coordinateEpoch);
                State = "Alarm";
                LastError = line;
                _pending?.TrySetException(new InvalidOperationException(line));
            }
        }
        if (!line.StartsWith("<") && !line.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke(line);
        }
        if (!line.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            Changed?.Invoke();
        }
    }

    private void ParseStatus(string line)
    {
        string[] fields = line[1..^1].Split('|');
        if (fields[0].StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) && !State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _coordinateEpoch);
        }
        State = fields[0];
        _statusTime = DateTime.UtcNow;
        if (State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) && IsRunning)
        {
            LastError = "GRBL ha segnalato un allarme.";
            _pending?.TrySetException(new InvalidOperationException(LastError));
        }
        MachinePosition? work = null;
        MachinePosition? machine = null;
        bool spindle = false;
        foreach (string field in fields.Skip(1))
        {
            if (field.StartsWith("WCO:") && ParsePosition(field[4..]) is MachinePosition offset)
            {
                _offset = offset;
            }
            else if (field.StartsWith("WPos:"))
            {
                work = ParsePosition(field[5..]);
            }
            else if (field.StartsWith("MPos:"))
            {
                machine = ParsePosition(field[5..]);
            }
            else if (field.StartsWith("Ov:"))
            {
                string[] ov = field[3..].Split(',');
                if (ov.Length >= 3 && int.TryParse(ov[0], out int feed) && int.TryParse(ov[2], out int spindleValue))
                {
                    FeedOverride = feed;
                    SpindleOverride = spindleValue;
                    _feedAwaitingReport = false;
                    _spindleAwaitingReport = false;
                    if (IsRunning)
                    {
                        SendPendingOverrides();
                    }
                }
            }
            else if (field.StartsWith("A:"))
            {
                spindle = field.Contains('S') || field.Contains('C');
            }
        }
        SpindleRunning = spindle;
        WorkPosition = work ?? (machine != null && _offset != null
            ? new MachinePosition(machine.X - _offset.X, machine.Y - _offset.Y, machine.Z - _offset.Z) : null);
        if (_holdRequested && !State.StartsWith("Hold", StringComparison.OrdinalIgnoreCase) && (State == "Run" || State == "Idle"))
        {
            // Clear the hold only after Resume was explicitly requested and GRBL left Hold.
            if (_resumeRequested)
            {
                _holdRequested = false;
                _resumeRequested = false;
                _clock.Start();
            }
        }
        if (State.StartsWith("Hold", StringComparison.OrdinalIgnoreCase))
        {
            _clock.Stop();
        }
    }

    private bool _resumeRequested;

    private static MachinePosition? ParsePosition(string text)
    {
        string[] values = text.Split(',');
        if (values.Length < 3 || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z) ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            return null;
        }
        return new MachinePosition(x, y, z);
    }

    private void OnFailure(Exception ex)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _coordinateEpoch);
            LastError = "Connessione persa: " + ex.Message;
            _connectionFault = true;
            State = "Disconnessa";
            _statusTime = DateTime.MinValue;
            _pending?.TrySetException(new IOException(LastError, ex));
            _runCancellation?.Cancel();
            _jogCancellation?.Cancel();
        }
        Log?.Invoke(LastError + ". Verifica subito la macchina.");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (IsRunning || IsJogging)
            {
                throw new InvalidOperationException("Interrompi la lavorazione prima di scollegare la CNC.");
            }
            _disposed = true;
            _transport.LineReceived -= OnLine;
            _transport.Failed -= OnFailure;
            _transport.Dispose();
            State = "Disconnessa";
        }
    }
}
