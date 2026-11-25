using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RonCUT;

internal static class WebcamMjpegTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellation = timeout.Token;
        using var server = new WebcamMjpegServer(0);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        string url = WebcamMjpegServer.MakeUrl(IPAddress.Loopback, server.Port);
        using var missing = await client.GetAsync(new Uri(new Uri(url), "/missing"), cancellation).ConfigureAwait(false);
        check(missing.StatusCode == HttpStatusCode.NotFound, "MJPEG: il server espone il solo endpoint /stream");

        // A response is read incrementally because it stays open until the viewer disconnects.
        using var first = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        using var second = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        check(first.IsSuccessStatusCode && second.IsSuccessStatusCode &&
            first.Content.Headers.ContentType?.ToString() == "multipart/x-mixed-replace; boundary=frame" &&
            second.Content.Headers.ContentType?.ToString() == first.Content.Headers.ContentType.ToString(),
            "MJPEG: tipo multipart e boundary frame per due connessioni simultanee");
        var stream1 = await first.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        var stream2 = await second.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);

        server.Publish(MakeImage(20), 75);
        byte[] firstJpeg = await ReadJpeg(stream1, cancellation).ConfigureAwait(false);
        byte[] secondJpeg = await ReadJpeg(stream2, cancellation).ConfigureAwait(false);
        using var jpegData = new MemoryStream(firstJpeg);
        var picture = new JpegBitmapDecoder(jpegData, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single();
        check(firstJpeg.SequenceEqual(secondJpeg) && picture.PixelWidth == 16 && picture.PixelHeight == 16,
            "MJPEG: lo stesso fotogramma JPEG decodificabile raggiunge entrambi i client");

        second.Dispose();
        server.Publish(MakeImage(200), 80);
        byte[] nextJpeg = await ReadJpeg(stream1, cancellation).ConfigureAwait(false);
        check(!firstJpeg.SequenceEqual(nextJpeg), "MJPEG: il flusso continua dopo la disconnessione di un client");
    }

    private static BitmapSource MakeImage(byte gray)
    {
        var bytes = new byte[16 * 16 * 3];
        Array.Fill(bytes, gray);
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr24, null, bytes, 16 * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task<byte[]> ReadJpeg(Stream stream, CancellationToken cancellation)
    {
        if (await ReadLine(stream, cancellation).ConfigureAwait(false) != "--frame" ||
            await ReadLine(stream, cancellation).ConfigureAwait(false) != "Content-Type: image/jpeg")
        {
            throw new InvalidDataException("Boundary o tipo del fotogramma MJPEG non valido.");
        }
        const string contentLength = "Content-Length: ";
        string header = await ReadLine(stream, cancellation).ConfigureAwait(false);
        if (!header.StartsWith(contentLength, StringComparison.Ordinal) ||
            !int.TryParse(header.AsSpan(contentLength.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
            length is < 4 or > 100_000 || await ReadLine(stream, cancellation).ConfigureAwait(false) != "")
        {
            throw new InvalidDataException("Lunghezza del fotogramma MJPEG non valida.");
        }
        var jpeg = new byte[length];
        await stream.ReadExactlyAsync(jpeg, cancellation).ConfigureAwait(false);
        if (jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[^2] != 0xff || jpeg[^1] != 0xd9 ||
            await ReadLine(stream, cancellation).ConfigureAwait(false) != "")
        {
            throw new InvalidDataException("Dati JPEG incompleti.");
        }
        return jpeg;
    }

    private static async Task<string> ReadLine(Stream stream, CancellationToken cancellation)
    {
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < 256)
        {
            await stream.ReadExactlyAsync(next, cancellation).ConfigureAwait(false);
            if (next[0] == '\n' && bytes.Count > 0 && bytes[^1] == '\r')
            {
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 1);
            }
            bytes.Add(next[0]);
        }
        throw new InvalidDataException("Intestazione MJPEG troppo lunga.");
    }
}
