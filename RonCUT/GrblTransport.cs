using System.IO.Ports;
using System.IO;
using System.Text;

namespace RonCUT;

public interface IGrblTransport : IDisposable
{
    event Action<string>? LineReceived;
    event Action<Exception>? Failed;
    bool IsOpen { get; }
    void Open();
    void Write(byte[] data);
}

public sealed class SerialGrblTransport : IGrblTransport
{
    private readonly SerialPort _port;
    private readonly object _writeLock = new object();
    private volatile bool _closing;
    public event Action<string>? LineReceived;
    public event Action<Exception>? Failed;
    public bool IsOpen => _port.IsOpen && !_closing;

    public SerialGrblTransport(string port, int baud)
    {
        _port = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            NewLine = "\n",
            ReadTimeout = 300,
            WriteTimeout = 1500,
            DtrEnable = false,
            RtsEnable = false
        };
    }

    public void Open()
    {
        _port.Open();
        _ = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        while (!_closing)
        {
            try
            {
                string line = _port.ReadLine().Trim();
                if (line.Length > 0)
                {
                    LineReceived?.Invoke(line);
                }
            }
            catch (TimeoutException)
            {
            }
            catch (Exception ex)
            {
                if (!_closing)
                {
                    Failed?.Invoke(ex);
                }
                return;
            }
        }
    }

    public void Write(byte[] data)
    {
        lock (_writeLock)
        {
            if (!IsOpen)
            {
                throw new IOException("Porta seriale non connessa.");
            }
            _port.Write(data, 0, data.Length);
        }
    }

    public void Dispose()
    {
        _closing = true;
        lock (_writeLock)
        {
            _port.Dispose();
        }
    }
}
