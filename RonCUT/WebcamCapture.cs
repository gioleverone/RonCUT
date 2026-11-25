using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace RonCUT;

internal sealed record WebcamDevice(string Id, string Name);

internal static class WebcamCapture
{
    private const SourceReaderIndex Video = SourceReaderIndex.FirstVideoStream;
    private static readonly Guid PackedYuy2 = new("32595559-0000-0010-8000-00aa00389b71");

    public static WebcamDevice[] FindDevices()
    {
        MFStartup().CheckError();
        try
        {
            using var attributes = MFCreateAttributes(1);
            attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap).CheckError();
            Marshal.ThrowExceptionForHR(EnumDeviceSources(attributes.NativePointer, out nint devices, out uint count));
            try
            {
                var result = new List<WebcamDevice>();
                for (int i = 0; i < count; i++)
                {
                    // Each activation returned by Media Foundation owns a reference.
                    using var device = new IMFActivate(Marshal.ReadIntPtr(devices, i * IntPtr.Size));
                    result.Add(new WebcamDevice(device.SymbolicLink, device.FriendlyName));
                }
                return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            finally
            {
                Marshal.FreeCoTaskMem(devices);
            }
        }
        finally
        {
            MFShutdown();
        }
    }

    public static void Run(string deviceId, Action<WebcamFormat, WebcamReader> consume, CancellationToken stop)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        try
        {
            MFStartup().CheckError();
            try
            {
                Open(deviceId, consume, stop);
            }
            finally
            {
                MFShutdown();
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static void Open(string deviceId, Action<WebcamFormat, WebcamReader> consume, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        using var device = MFCreateAttributes(2);
        device.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap).CheckError();
        device.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, deviceId).CheckError();
        using var source = MFCreateDeviceSource(device);
        try
        {
            using var callback = new WebcamReader();
            using var options = MFCreateAttributes(3);
            options.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true).CheckError();
            options.Set(SourceReaderAttributeKeys.DisconnectMediasourceOnShutdown, true).CheckError();
            options.AsyncCallback = callback;
            using var reader = MFCreateSourceReaderFromMediaSource(source, options);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(Video, true);
            callback.Reader = reader;
            consume(SelectFormat(reader), callback);
        }
        finally
        {
            CloseSource(source);
        }
    }

    private static void CloseSource(IMFMediaSource source)
    {
        try
        {
            source.Shutdown();
        }
        catch (SharpGenException)
        {
            // Windows may have already shut the source down after unplugging the camera.
        }
    }

    private static WebcamFormat SelectFormat(IMFSourceReader reader)
    {
        var modes = new List<(int Index, WebcamFormat Format, double Score)>();
        for (int i = 0; ; i++)
        {
            try
            {
                using var native = reader.GetNativeMediaType(Video, i);
                var format = WebcamFormat.Read(native);
                if (format.Width is < 64 or > 4096 || format.Height is < 64 or > 4096 ||
                    (format.Width % 2) != 0 || (format.Height % 2) != 0 ||
                    format.RateDenominator == 0 || format.FramesPerSecond is < 1 or > 60)
                {
                    continue;
                }
                double score = Math.Abs(Math.Log((double)format.Width / 640)) + Math.Abs(Math.Log((double)format.Height / 480))
                    + Math.Abs(format.FramesPerSecond - 15) / 50
                    + (native.GetGUID(MediaTypeAttributeKeys.Subtype) == PackedYuy2 ? 0 : 0.1);
                modes.Add((i, format, score));
            }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.NoMoreTypes)
            {
                break;
            }
            catch (SharpGenException ex) when (ex.ResultCode == ResultCode.Attributenotfound)
            {
                // The device can also advertise metadata streams with no video dimensions.
            }
        }

        foreach (var mode in modes.OrderBy(mode => mode.Score))
        {
            try
            {
                using var native = reader.GetNativeMediaType(Video, mode.Index);
                reader.SetCurrentMediaType(Video, native);
                if (native.GetGUID(MediaTypeAttributeKeys.Subtype) != PackedYuy2)
                {
                    using var output = mode.Format.CreateMediaType(PackedYuy2);
                    reader.SetCurrentMediaType(Video, output);
                }
                using var selected = reader.GetCurrentMediaType(Video);
                return WebcamFormat.Read(selected);
            }
            catch (SharpGenException)
            {
                // Try the next native mode when this driver does not support the conversion.
            }
        }
        throw new IOException("Nessun formato video compatibile per questa webcam.");
    }

    public static unsafe BitmapSource CreateBitmap(IMFSample sample, WebcamFormat format, int maxWidth = int.MaxValue)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out nint data, out _, out int length);
        try
        {
            int inputStride = format.Width * 2;
            if (length < inputStride * format.Height)
            {
                throw new IOException("La webcam ha restituito un fotogramma incompleto.");
            }
            var pixels = new ReadOnlySpan<byte>((void*)data, length);
            int width = Math.Min(maxWidth, format.Width);
            int height = Math.Max(1, width * format.Height / format.Width);
            int stride = width * 3;
            var rgb = new byte[height * stride];
            for (int y = 0; y < height; y++)
            {
                int row = (y * format.Height / height) * inputStride;
                for (int x = 0; x < width; x++)
                {
                    int inputX = x * format.Width / width;
                    int pair = row + (inputX / 2) * 4;
                    int luma = Math.Max(0, pixels[row + inputX * 2] - 16);
                    int u = pixels[pair + 1] - 128;
                    int v = pixels[pair + 3] - 128;
                    int output = y * stride + x * 3;
                    rgb[output] = ColorByte(298 * luma + 516 * u);
                    rgb[output + 1] = ColorByte(298 * luma - 100 * u - 208 * v);
                    rgb[output + 2] = ColorByte(298 * luma + 409 * v);
                }
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, rgb, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static byte ColorByte(int value) => (byte)Math.Clamp((value + 128) >> 8, 0, 255);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint mode);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("mf.dll", EntryPoint = "MFEnumDeviceSources")]
    private static extern int EnumDeviceSources(nint attributes, out nint devices, out uint count);
}

internal sealed class WebcamReader : IMFSourceReaderCallbackBase
{
    private readonly object _gate = new();
    private TaskCompletionSource<SampleResult>? _pending;
    public IMFSourceReader Reader { private get; set; } = null!;

    public void ReadFrames(Action<IMFSample, long> frame, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var request = new TaskCompletionSource<SampleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending = request;
            }
            try
            {
                RequestFrame(Reader);
                var result = request.Task.WaitAsync(TimeSpan.FromSeconds(5), stop).GetAwaiter().GetResult();
                result.Status.CheckError();
                if ((result.Flags & (SourceReaderFlag.Error | SourceReaderFlag.EndOfStream | SourceReaderFlag.CurrentMediaTypeChanged)) != 0)
                {
                    throw new IOException("La webcam ha interrotto il video o cambiato formato.");
                }
                if (result.Sample != null && !stop.IsCancellationRequested)
                {
                    frame(result.Sample, result.Timestamp);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _pending = null;
                    if (request.Task.IsCompletedSuccessfully)
                    {
                        request.Task.Result.Sample?.Dispose();
                    }
                }
            }
        }
    }

    public override void OnReadSample(Result status, SourceReaderIndex stream, SourceReaderFlag flags, long timestamp, IMFSample sample)
    {
        lock (_gate)
        {
            if (_pending is not { Task.IsCompleted: false } request)
            {
                return;
            }
            IMFSample? owned = null;
            if (sample != null)
            {
                // Media Foundation lends the callback its sample. Keep a reference for the capture worker.
                nint pointer = sample.NativePointer;
                Marshal.AddRef(pointer);
                owned = new IMFSample(pointer);
            }
            request.SetResult(new SampleResult(status, flags, timestamp, owned));
        }
    }

    private static unsafe void RequestFrame(IMFSourceReader reader)
    {
        nint pointer = reader.NativePointer;
        // IMFSourceReader::ReadSample requires null output pointers when it is used with a callback.
        var read = (delegate* unmanaged[Stdcall]<nint, int, int, nint, nint, nint, nint, int>)(*(nint**)pointer)[9];
        new Result(read(pointer, (int)SourceReaderIndex.FirstVideoStream, 0, 0, 0, 0, 0)).CheckError();
    }

    private readonly record struct SampleResult(Result Status, SourceReaderFlag Flags, long Timestamp, IMFSample? Sample);
}

internal sealed record WebcamFormat(int Width, int Height, uint RateNumerator, uint RateDenominator, Guid Subtype)
{
    public double FramesPerSecond => (double)RateNumerator / RateDenominator;

    public static WebcamFormat Read(IMFMediaType type)
    {
        MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out uint width, out uint height).CheckError();
        MFGetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, out uint numerator, out uint denominator).CheckError();
        return new WebcamFormat((int)width, (int)height, numerator, denominator, type.GetGUID(MediaTypeAttributeKeys.Subtype));
    }

    public IMFMediaType CreateMediaType(Guid subtype)
    {
        var type = MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)Width, (uint)Height).CheckError();
            MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, RateNumerator, RateDenominator).CheckError();
            MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }
}
