using System.IO;
using System.Text.Json;

namespace RonCUT;

public sealed class GeneralSettings
{
    public double SafeZ { get; set; } = 10;
    public double TabLength { get; set; } = 2;
    public string CommunicationsApiUrl { get; set; } = "";
    public string WebcamId { get; set; } = "";
    public string WebcamName { get; set; } = "";
    public int WebcamHttpPort { get; set; } = WebcamMjpegServer.DefaultPort;
    public int WebcamFps { get; set; } = WebcamStreamSettings.DefaultFps;
    public int WebcamJpegQuality { get; set; } = WebcamStreamSettings.DefaultQuality;

    internal WebcamStreamSettings? GetWebcamStream()
    {
        // A malformed webcam setting must not prevent opening or exporting a CNC project.
        return !string.IsNullOrWhiteSpace(WebcamId) && WebcamStreamSettings.IsValid(WebcamHttpPort, WebcamFps, WebcamJpegQuality)
            ? new WebcamStreamSettings(new WebcamDevice(WebcamId, WebcamName), WebcamHttpPort, WebcamFps, WebcamJpegQuality)
            : null;
    }

    internal GeneralSettings Copy() => (GeneralSettings)MemberwiseClone();

    public void Validate()
    {
        if (!double.IsFinite(SafeZ) || SafeZ <= 0 || SafeZ > 1000)
        {
            throw new InvalidOperationException("La Z di sicurezza deve essere maggiore di zero e non superare 1000 mm.");
        }
        if (!double.IsFinite(TabLength) || TabLength <= 0 || TabLength > 1000)
        {
            throw new InvalidOperationException("La lunghezza dei ponticelli deve essere maggiore di zero e non superare 1000 mm.");
        }
        ValidateUrl(CommunicationsApiUrl);
    }

    private static void ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        string text = value.Trim();
        if (text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException("Inserisci un URL API REST completo (http:// o https://) oppure lascia il campo vuoto.");
        }
    }

    public void Apply(Job job)
    {
        Validate();
        job.SafeZ = SafeZ;
        foreach (var operation in job.Operations)
        {
            operation.TabLength = TabLength;
        }
    }

    public static GeneralSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new GeneralSettings();
        }
        string json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(nameof(SafeZ), out _) ||
            !document.RootElement.TryGetProperty(nameof(TabLength), out _))
        {
            throw new InvalidOperationException("Il file delle impostazioni generali è incompleto.");
        }
        var settings = JsonSerializer.Deserialize<GeneralSettings>(json)
            ?? throw new InvalidOperationException("Il file delle impostazioni generali è vuoto.");
        settings.Validate();
        return settings;
    }
}
