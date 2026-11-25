using System.Diagnostics;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.Connections;

namespace RonCUT;

internal sealed class WebcamSession
{
    private readonly WebcamStreamSettings? _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _capture;
    private Task? _stopped;
    private volatile bool _previewEnabled;
    private volatile WebcamView _view = new();

    public WebcamDevice Device { get; }
    public WebcamView View => _view;
    public bool PreviewEnabled { set => _previewEnabled = value; }

    public WebcamSession(WebcamDevice device, WebcamStreamSettings? stream, Task after)
    {
        Device = device;
        _settings = stream;
        _previewEnabled = stream == null;
        _capture = Task.Run(() => RunAsync(after, _stop.Token));
    }

    // The owner calls Stop on the dispatcher. Replacements wait until the driver releases the device.
    public Task Stop() => _stopped ??= StopAsync();

    private async Task StopAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _capture.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task RunAsync(Task after, CancellationToken stop)
    {
        await after.ConfigureAwait(false);
        while (!stop.IsCancellationRequested)
        {
            _view = new WebcamView();
            try
            {
                WebcamCapture.Run(Device.Id, (format, reader) => CaptureVideo(format, reader, stop), stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                string message = ex.HResult == unchecked((int)0x80070005)
                    ? "Windows ha negato l’accesso alla webcam. Verifica i permessi della fotocamera."
                    : "Webcam non disponibile. Controlla il collegamento e se è in uso. RonCUT riprova automaticamente.";
                _view = new WebcamView(CameraError: message);
                Debug.WriteLine(ex);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CaptureVideo(WebcamFormat format, WebcamReader reader, CancellationToken stop)
    {
        WebcamMjpegServer? server = null;
        var clock = Stopwatch.StartNew();
        long nextPreview = 0;
        long nextAddress = 0;
        long nextStart = 0;
        long lastFrameSlot = -1;
        try
        {
            reader.ReadFrames((sample, _) =>
            {
                long now = clock.ElapsedMilliseconds;
                bool preview = _previewEnabled && now >= nextPreview;
                BitmapSource? bitmap = null;
                if (_settings != null && now >= nextStart)
                {
                    try
                    {
                        server ??= new WebcamMjpegServer(_settings.Port);
                        long slot = now * _settings.Fps / 1000;
                        if (slot != lastFrameSlot)
                        {
                            bitmap = WebcamCapture.CreateBitmap(sample, format);
                            server.Publish(bitmap, _settings.Quality);
                            lastFrameSlot = slot;
                        }
                        if (now >= nextAddress)
                        {
                            _view = _view with { Url = server.RefreshAddress(), Streaming = true, StreamError = null };
                            nextAddress = now + 3000;
                        }
                    }
                    catch (Exception ex)
                    {
                        server?.Dispose();
                        server = null;
                        nextStart = now + 3000;
                        nextAddress = 0;
                        lastFrameSlot = -1;
                        _view = _view with
                        {
                            Url = null,
                            Streaming = false,
                            StreamError = ex is AddressInUseException || ex.InnerException is AddressInUseException
                                ? $"La porta HTTP {_settings.Port} è già in uso. Puoi sceglierne un’altra."
                                : "Impossibile trasmettere il video MJPEG. RonCUT riprova automaticamente."
                        };
                        Debug.WriteLine(ex);
                    }
                }
                if (preview)
                {
                    _view = _view with { Frame = bitmap ?? WebcamCapture.CreateBitmap(sample, format, 400) };
                    nextPreview = now + 100;
                }
                else if (!_previewEnabled && _view.Frame != null)
                {
                    _view = _view with { Frame = null };
                }
            }, stop);
        }
        finally
        {
            server?.Dispose();
        }
    }
}

internal sealed record WebcamView(BitmapSource? Frame = null, string? CameraError = null, string? StreamError = null,
    string? Url = null, bool Streaming = false);
