using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RonCUT;

public sealed class Library
{
    public ObservableCollection<Tool> Tools { get; set; } = new();
    public ObservableCollection<Material> Materials { get; set; } = new();
    public ObservableCollection<Profile> Profiles { get; set; } = new();

    public static Library CreateDefault()
    {
        var library = new Library();
        library.Tools.Add(new Tool());
        library.Materials.Add(new Material());
        library.Materials.Add(new Material { Name = "Lamellare" });
        library.Profiles.Add(new Profile
        {
            ToolName = library.Tools[0].Name,
            MaterialName = library.Materials[0].Name,
            Parameters = new CuttingParameters { FeedXY = 450 }
        });
        library.Profiles.Add(new Profile
        {
            ToolName = library.Tools[0].Name,
            MaterialName = library.Materials[1].Name,
            Parameters = new CuttingParameters { StepDown = 1.5, FeedXY = 150, FeedZ = 60 }
        });
        library.ResolveLegacyReferences();
        return library;
    }

    public static Library Read(string json)
    {
        var library = JsonSerializer.Deserialize<Library>(json) ?? throw new InvalidOperationException("Archivio vuoto.");
        if (library.Tools.Count == 0 || library.Materials.Count == 0)
        {
            throw new InvalidOperationException("L'archivio deve contenere almeno un utensile e un supporto.");
        }
        library.ResolveLegacyReferences();
        return library;
    }

    public Tool? FindTool(Tool saved) => saved.Id == Guid.Empty
        ? Tools.FirstOrDefault(tool => SameName(tool.Name, saved.Name) && SameToolGeometry(tool, saved))
        : Tools.FirstOrDefault(tool => tool.Id == saved.Id);

    public Material? FindMaterial(Guid id, string name) => id == Guid.Empty
        ? Materials.FirstOrDefault(material => SameName(material.Name, name))
        : Materials.FirstOrDefault(material => material.Id == id);

    public Profile? FindProfile(Guid toolId, Guid materialId) => toolId == Guid.Empty || materialId == Guid.Empty
        ? null : Profiles.FirstOrDefault(profile => profile.ToolId == toolId && profile.MaterialId == materialId);

    public static bool SameToolGeometry(Tool first, Tool second) =>
        first.Diameter == second.Diameter && first.Flutes == second.Flutes;

    public static bool SameName(string? first, string? second) =>
        string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void ResolveLegacyReferences()
    {
        foreach (var tool in Tools.Where(tool => tool.Id == Guid.Empty))
        {
            tool.Id = LegacyId(nameof(Tool), tool.Name);
        }
        foreach (var material in Materials.Where(material => material.Id == Guid.Empty))
        {
            material.Id = LegacyId(nameof(Material), material.Name);
        }
        foreach (var profile in Profiles)
        {
            var tool = profile.ToolId == Guid.Empty
                ? Tools.SingleOrDefault(tool => SameName(tool.Name, profile.ToolName))
                : Tools.SingleOrDefault(tool => tool.Id == profile.ToolId);
            if (tool != null)
            {
                profile.ToolId = tool.Id;
                profile.ToolName = null;
            }
            var material = FindMaterial(profile.MaterialId, profile.MaterialName ?? "");
            if (material != null)
            {
                profile.MaterialId = material.Id;
                profile.MaterialName = null;
            }
        }
    }

    private static Guid LegacyId(string type, string name)
    {
        // Keep identities across restarts before the migrated archive is first saved.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("RonCUT/" + type + "/" + name.Trim().ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
