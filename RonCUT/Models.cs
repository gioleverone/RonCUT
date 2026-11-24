using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace RonCUT;

public sealed class Job
{
    public int Version { get; set; } = 1;
    public string SvgName { get; set; } = "";
    public string SvgText { get; set; } = "";
    public double SvgUnitMm { get; set; } = 25.4 / 72.0;
    public List<Contour> Contours { get; set; } = new();
    public List<Operation> Operations { get; set; } = new();
    public Tool Tool { get; set; } = new();
    public Material Material { get; set; } = new();
    public CuttingParameters Parameters { get; set; } = new();
    public bool ProfileConfirmed { get; set; } = true;
    public double ZeroX
    {
        get; set;
    }
    public double ZeroY
    {
        get; set;
    }
    public double SafeZ { get; set; } = 10;
    public bool SpindleCommands
    {
        get; set;
    }
}

public sealed class Pt
{
    public double X
    {
        get; set;
    }
    public double Y
    {
        get; set;
    }
    public Pt()
    {
    }
    public Pt(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public sealed class Contour
{
    public string Id { get; set; } = "";
    public bool Closed
    {
        get; set;
    }
    public List<Pt> Points { get; set; } = new();
}

public sealed class Operation
{
    public const string ToolChangeKind = "Pausa cambio utensile";
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string ContourId { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Depth { get; set; } = 3;
    public double TabLength { get; set; } = 2;
    public double TabHeight { get; set; } = 1;
    public List<Pt> Tabs { get; set; } = new();
    public List<string> IslandContourIds { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ToolChange? NextTool { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsToolChange => Kind == ToolChangeKind;
}

public sealed class ToolChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid MaterialId { get; set; }
    public string MaterialName { get; set; } = "";
    public Tool Tool { get; set; } = new();
    public CuttingParameters Parameters { get; set; } = new();
}

public sealed class Tool : INotifyPropertyChanged
{
    private string _name = "Fresa 3 mm";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; set; }
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public double Diameter { get; set; } = 3;
    public int Flutes { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Name + " · Ø " + Diameter.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) + " mm";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class Material : INotifyPropertyChanged
{
    private string _name = "Multistrato";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Id { get; set; }
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CuttingParameters
{
    public double StepDown { get; set; } = 2;
    public double FeedXY { get; set; } = 250;
    public double FeedZ { get; set; } = 60;
    public double SpindleRpm { get; set; } = 18000;
    public double StepOverPercent { get; set; } = 50;
}

public sealed class Profile
{
    public Guid ToolId { get; set; }
    public Guid MaterialId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaterialName { get; set; }
    public CuttingParameters Parameters { get; set; } = new();
}
