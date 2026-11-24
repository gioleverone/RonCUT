using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RonCUT;

public class JogProfile
{
    public double Step { get; set; }
    public double Feed { get; set; }
}

public sealed class CutSettings
{
    public string Port { get; set; } = "";
    public int Baud { get; set; } = 115200;
    public string SelectedProfile { get; set; } = nameof(Slow);
    public JogProfile Slow { get; set; } = new JogProfile { Step = 0.1, Feed = 100 };
    public JogProfile Fast { get; set; } = new JogProfile { Step = 10, Feed = 500 };

    [JsonIgnore]
    public JogProfile ActiveProfile => SelectedProfile == nameof(Fast) ? Fast : Slow;

    private sealed class LegacyJogProfile : JogProfile
    {
        public string Name { get; set; } = "";
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RonCUT", "impostazioni-cut.json");

    public void Validate()
    {
        if (Baud < 1200 || Baud > 1000000 || Port == null)
        {
            throw new InvalidOperationException("Impostazioni CUT non valide.");
        }
        if (!ValidProfile(Slow) || !ValidProfile(Fast))
        {
            throw new InvalidOperationException("Controlla passo e velocità di Slow e Fast.");
        }
        SelectedProfile = IsProfileName(SelectedProfile, nameof(Fast)) ? nameof(Fast) : nameof(Slow);
    }

    private static bool ValidProfile(JogProfile? profile) =>
        profile != null && profile.Step is > 0 and <= 1000 && profile.Feed is > 0 and <= 100000;

    private static bool IsProfileName(string? value, string name) =>
        string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    public static CutSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CutSettings();
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var settings = root.Deserialize<CutSettings>() ?? throw new InvalidOperationException("Impostazioni CUT vuote.");
        if (root.TryGetProperty("Profiles", out var profiles))
        {
            var legacy = profiles.Deserialize<LegacyJogProfile[]>() ?? Array.Empty<LegacyJogProfile>();
            if (!root.TryGetProperty(nameof(Slow), out _))
            {
                settings.Slow = ImportProfile(legacy, nameof(Slow), settings.Slow);
            }
            if (!root.TryGetProperty(nameof(Fast), out _))
            {
                settings.Fast = ImportProfile(legacy, nameof(Fast), settings.Fast);
            }
        }
        settings.Validate();
        return settings;
    }

    private static JogProfile ImportProfile(LegacyJogProfile[] profiles, string name, JogProfile fallback)
    {
        var profile = profiles.SingleOrDefault(profile => IsProfileName(profile?.Name, name));
        return profile == null ? fallback : new JogProfile { Step = profile.Step, Feed = profile.Feed };
    }

    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
