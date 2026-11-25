using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace RonCUT;

internal sealed record WebcamStreamSettings(WebcamDevice Device, int Port, int Fps, int Quality)
{
    public const int DefaultFps = 10;
    public const int DefaultQuality = 75;
    public const string RangeHelp = "Inserisci porta HTTP (1–65535), FPS (1–30) e qualità JPEG (1–100).";

    public static bool IsValid(int port, int fps, int quality) =>
        port is >= 1 and <= 65535 && fps is >= 1 and <= 30 && quality is >= 1 and <= 100;
}

internal sealed class WebcamMjpegServer : IDisposable
{
    public const int DefaultPort = 8554;
    private const string StreamPath = "/stream";
    private const string StreamType = "multipart/x-mixed-replace; boundary=frame";
    private readonly IWebHost _host;
    private readonly CancellationTokenSource _close = new();
    private readonly object _frames = new();
    private TaskCompletionSource<MjpegFrame> _next = NewFrameSignal();
    private MjpegFrame? _latest;
    private WebcamLan[] _networks = [];

    public int Port { get; }

    public WebcamMjpegServer(int port = DefaultPort)
    {
        _host = new WebHostBuilder()
            .SuppressStatusMessages(true)
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseKestrel(options =>
            {
                options.Listen(IPAddress.Any, port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
                options.AddServerHeader = false;
                options.Limits.MaxConcurrentConnections = 24;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            })
            .Configure(app => app.Run(ServeAsync))
            .Build();
        try
        {
            _networks = WebcamLan.Find();
            _host.StartAsync().GetAwaiter().GetResult();
            Port = new Uri(_host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        }
        catch
        {
            _host.Dispose();
            _close.Dispose();
            throw;
        }
    }

    public static string MakeUrl(IPAddress address, int port = DefaultPort) => $"http://{address}:{port}{StreamPath}";

    public static IPAddress? FindLocalAddress() => WebcamLan.Find().FirstOrDefault()?.Address;

    public string? RefreshAddress()
    {
        var networks = WebcamLan.Find();
        Volatile.Write(ref _networks, networks);
        return networks.Length > 0 ? MakeUrl(networks[0].Address, Port) : null;
    }

    public void Publish(BitmapSource bitmap, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        byte[] jpeg = output.ToArray();
        byte[] header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
        var frame = new MjpegFrame(header, jpeg);
        lock (_frames)
        {
            _latest = frame;
            _next.SetResult(frame);
            _next = NewFrameSignal();
        }
    }

    private async Task ServeAsync(HttpContext context)
    {
        if (context.Request.Path != StreamPath)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!IsLocal(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        bool head = HttpMethods.IsHead(context.Request.Method);
        if (!head && !HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        var response = context.Response;
        response.ContentType = StreamType;
        response.Headers.CacheControl = "no-store";
        if (head)
        {
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _close.Token);
        try
        {
            await response.BodyWriter.FlushAsync(stop.Token).ConfigureAwait(false);
            MjpegFrame? previous = null;
            while (!stop.IsCancellationRequested)
            {
                Task<MjpegFrame> next;
                lock (_frames)
                {
                    next = _latest != null && _latest != previous ? Task.FromResult(_latest) : _next.Task;
                }
                var frame = await next.WaitAsync(stop.Token).ConfigureAwait(false);
                response.BodyWriter.Write(frame.Header);
                response.BodyWriter.Write(frame.Jpeg);
                response.BodyWriter.Write("\r\n"u8);
                var flush = await response.BodyWriter.FlushAsync(stop.Token).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    return;
                }
                previous = frame;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // A client can close the connection while a frame is being sent.
        }
    }

    private bool IsLocal(IPAddress? address)
    {
        return address != null && (IPAddress.IsLoopback(address) ||
            Volatile.Read(ref _networks).Any(network => network.Subnet.Contains(address)));
    }

    public void Dispose()
    {
        _close.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _host.StopAsync(timeout.Token).GetAwaiter().GetResult();
        }
        finally
        {
            _host.Dispose();
            _close.Dispose();
        }
    }

    private static TaskCompletionSource<MjpegFrame> NewFrameSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record MjpegFrame(byte[] Header, byte[] Jpeg);

    private sealed record WebcamLan(IPAddress Address, IPNetwork Subnet, bool HasGateway)
    {
        public static WebcamLan[] Find()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)
                .Select(adapter => adapter.GetIPProperties())
                .SelectMany(properties => properties.UnicastAddresses
                    .Where(info => info.Address.AddressFamily == AddressFamily.InterNetwork && info.PrefixLength is > 0 and <= 32)
                    .Select(info => new WebcamLan(info.Address, GetSubnet(info.Address, info.PrefixLength),
                        properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))))
                .OrderByDescending(network => network.HasGateway)
                .ToArray();
        }

        private static IPNetwork GetSubnet(IPAddress address, int prefix)
        {
            byte[] bytes = address.GetAddressBytes();
            uint network = BinaryPrimitives.ReadUInt32BigEndian(bytes) & (uint.MaxValue << (32 - prefix));
            BinaryPrimitives.WriteUInt32BigEndian(bytes, network);
            return new IPNetwork(new IPAddress(bytes), prefix);
        }
    }
}
