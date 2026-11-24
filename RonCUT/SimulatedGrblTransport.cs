using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RonCUT;

public sealed class SimulatedGrblTransport : IGrblTransport
{
    private readonly object _gate = new object();
    private readonly Queue<string> _queue = new Queue<string>();
    private MachinePosition _position = new MachinePosition(0, 0, 0);
    private bool _open;
    private bool _processing;
    private bool _jogging;
    private bool _hold;
    private bool _spindle;
    private bool _relative;
    private double _feed = 200;
    private int _feedOverride = 100;
    private int _spindleOverride = 100;
    private long _generation;
    public event Action<string>? LineReceived;
    public event Action<Exception>? Failed;
    public bool IsOpen => _open;

    public void Open()
    {
        _open = true;
        LineReceived?.Invoke("Grbl 1.1h [SIMULAZIONE]");
    }

    public void Write(byte[] data)
    {
        if (!_open)
        {
            throw new InvalidOperationException("Simulatore disconnesso.");
        }
        if (data.Length == 1)
        {
            byte b = data[0];
            string? response = null;
            lock (_gate)
            {
                switch (b)
                {
                    case (byte)'?': response = Status(); break;
                    case (byte)'!': _hold = true; break;
                    case (byte)'~': _hold = false; break;
                    case 0x18:
                        _generation++;
                        _queue.Clear();
                        _processing = false;
                        _hold = false;
                        _spindle = false;
                        _relative = false;
                        _jogging = false;
                        response = "Grbl 1.1h [SIMULAZIONE]";
                        break;
                    case 0x85:
                        if (_jogging)
                        {
                            _generation++;
                            _queue.Clear();
                            _processing = false;
                            _hold = false;
                            _jogging = false;
                        }
                        break;
                    case 0x90: _feedOverride = 100; break;
                    case 0x91: _feedOverride = Math.Min(200, _feedOverride + 10); break;
                    case 0x92: _feedOverride = Math.Max(10, _feedOverride - 10); break;
                    case 0x93: _feedOverride = Math.Min(200, _feedOverride + 1); break;
                    case 0x94: _feedOverride = Math.Max(10, _feedOverride - 1); break;
                    case 0x99: _spindleOverride = 100; break;
                    case 0x9A: _spindleOverride = Math.Min(200, _spindleOverride + 10); break;
                    case 0x9B: _spindleOverride = Math.Max(10, _spindleOverride - 10); break;
                    case 0x9C: _spindleOverride = Math.Min(200, _spindleOverride + 1); break;
                    case 0x9D: _spindleOverride = Math.Max(10, _spindleOverride - 1); break;
                }
            }
            if (response != null)
            {
                LineReceived?.Invoke(response);
            }
            return;
        }
        string line = Encoding.ASCII.GetString(data).Trim();
        if (line == "$I")
        {
            LineReceived?.Invoke("[VER:1.1h:SIMULAZIONE]");
            LineReceived?.Invoke("ok");
            return;
        }
        lock (_gate)
        {
            if (line.StartsWith("$J=", StringComparison.OrdinalIgnoreCase))
            {
                _jogging = true;
            }
            _queue.Enqueue(line);
            if (!_processing)
            {
                _processing = true;
                long generation = _generation;
                _ = Task.Run(() => ProcessQueue(generation));
            }
        }
        if (line != "G4P0")
        {
            LineReceived?.Invoke("ok");
        }
    }

    private string Status()
    {
        string state = _hold ? "Hold:0" : _processing ? (_jogging ? "Jog" : "Run") : "Idle";
        string xyz = string.Join(",", new[] { _position.X, _position.Y, _position.Z }.Select(n => n.ToString("0.000", CultureInfo.InvariantCulture)));
        return "<" + state + "|WPos:" + xyz + "|Ov:" + _feedOverride + ",100," + _spindleOverride + (_spindle ? "|A:S" : "") + ">";
    }

    private async Task ProcessQueue(long generation)
    {
        try
        {
            while (true)
            {
                string line;
                lock (_gate)
                {
                    if (!_open || generation != _generation)
                    {
                        return;
                    }
                    if (_queue.Count == 0)
                    {
                        _processing = false;
                        _jogging = false;
                        return;
                    }
                    line = _queue.Dequeue();
                }
                while (true)
                {
                    bool paused;
                    lock (_gate)
                    {
                        if (!_open || generation != _generation)
                        {
                            return;
                        }
                        paused = _hold;
                    }
                    if (!paused)
                    {
                        break;
                    }
                    await Task.Delay(40);
                }
                await Execute(line, generation);
                if (line == "G4P0")
                {
                    bool finished;
                    lock (_gate)
                    {
                        if (!_open || generation != _generation)
                        {
                            return;
                        }
                        finished = _queue.Count == 0;
                        if (finished)
                        {
                            _processing = false;
                            _jogging = false;
                        }
                    }
                    LineReceived?.Invoke("ok");
                    if (finished)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    private async Task Execute(string line, long generation)
    {
        bool isJog = line.StartsWith("$J=", StringComparison.OrdinalIgnoreCase);
        bool relative;
        MachinePosition start;
        lock (_gate)
        {
            if (line == "G90")
            {
                _relative = false;
            }
            if (line == "G91")
            {
                _relative = true;
            }
            if (line.StartsWith("M3", StringComparison.OrdinalIgnoreCase))
            {
                _spindle = true;
            }
            if (line.StartsWith("M5", StringComparison.OrdinalIgnoreCase) || line.StartsWith("M30", StringComparison.OrdinalIgnoreCase))
            {
                _spindle = false;
            }
            if (line.StartsWith("M30", StringComparison.OrdinalIgnoreCase))
            {
                _feedOverride = 100;
                _spindleOverride = 100;
            }
            if (line.StartsWith("G92", StringComparison.OrdinalIgnoreCase))
            {
                _position = new MachinePosition(line.Contains('X') ? 0 : _position.X, line.Contains('Y') ? 0 : _position.Y, line.Contains('Z') ? 0 : _position.Z);
                return;
            }
            relative = isJog || _relative;
            start = _position;
        }
        bool rapid = line.StartsWith("G0 ", StringComparison.OrdinalIgnoreCase);
        bool move = rapid || line.StartsWith("G1 ", StringComparison.OrdinalIgnoreCase) || isJog;
        if (!move)
        {
            return;
        }
        double x = start.X, y = start.Y, z = start.Z;
        foreach (Match m in Regex.Matches(line, @"([XYZF])([-+]?\d+(?:\.\d+)?)"))
        {
            double value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[1].Value)
            {
                case "X": x = relative ? start.X + value : value; break;
                case "Y": y = relative ? start.Y + value : value; break;
                case "Z": z = relative ? start.Z + value : value; break;
                case "F": _feed = value; break;
            }
        }
        double dx = x - start.X, dy = y - start.Y, dz = z - start.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (distance < 0.000001)
        {
            return;
        }
        double progress = 0;
        while (progress < 1)
        {
            await Task.Delay(40);
            lock (_gate)
            {
                if (!_open || generation != _generation)
                {
                    return;
                }
                if (!_hold)
                {
                    double speed = rapid ? 2000 : _feed * _feedOverride / 100.0;
                    progress = Math.Min(1, progress + speed / 60 * 0.04 / distance);
                    _position = new MachinePosition(start.X + dx * progress, start.Y + dy * progress, start.Z + dz * progress);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _open = false;
            _generation++;
            _queue.Clear();
        }
    }
}
