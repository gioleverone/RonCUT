using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RonCUT;

public sealed class LibraryWindow : Window
{
    public Library Value
    {
        get; private set;
    }
    private readonly DataGrid _tools;
    private readonly DataGrid _materials;
    private readonly DataGrid _profiles;

    public LibraryWindow(Library original)
    {
        Value = Library.Read(JsonSerializer.Serialize(original));
        Title = "RonCUT — utensili e supporti";
        Width = 920;
        Height = 570;
        MinWidth = 700;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var cancel = new Button { Content = "Annulla", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Salva archivio", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        save.Click += Save_Click;
        bottom.Children.Add(cancel);
        bottom.Children.Add(save);
        root.Children.Add(bottom);
        var oldNames = Value.Profiles
            .Where(profile => profile.ToolName != null || profile.MaterialName != null)
            .Select(profile => string.Join(" / ", new[] { profile.ToolName, profile.MaterialName }.OfType<string>()))
            .Distinct().ToArray();
        var hint = new TextBlock
        {
            Text = "Scegli utensile e supporto dai menu dei profili. Puoi rinominarli nelle rispettive schede. "
                + "Quando elimini una voce, vengono eliminati anche i profili associati."
                + (oldNames.Length == 0 ? "" : "\nCompleta o elimina i profili con riferimenti mancanti nel vecchio archivio (" + string.Join(", ", oldNames) + ")."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);
        var tabs = new TabControl();
        _tools = Grid(Value.Tools);
        Value.Tools.CollectionChanged += ToolsChanged;
        AddColumn(_tools, "Nome", "Name", 2);
        AddColumn(_tools, "Diametro (mm)", "Diameter", 1);
        AddColumn(_tools, "Taglienti", "Flutes", 1);
        tabs.Items.Add(new TabItem { Header = "Utensili", Content = _tools });
        _materials = Grid(Value.Materials);
        Value.Materials.CollectionChanged += MaterialsChanged;
        AddColumn(_materials, "Nome supporto", "Name", 1);
        tabs.Items.Add(new TabItem { Header = "Supporti", Content = _materials });
        _profiles = Grid(Value.Profiles);
        AddChoiceColumn(_profiles, "Utensile", nameof(Profile.ToolId), Value.Tools);
        AddChoiceColumn(_profiles, "Supporto", nameof(Profile.MaterialId), Value.Materials);
        AddColumn(_profiles, "Avanz. Z mm/pass.", "Parameters.StepDown", 1);
        AddColumn(_profiles, "XY mm/min", "Parameters.FeedXY", 1);
        AddColumn(_profiles, "Z mm/min", "Parameters.FeedZ", 1);
        AddColumn(_profiles, "Mandrino giri/min", "Parameters.SpindleRpm", 1);
        AddColumn(_profiles, "Passo %", "Parameters.StepOverPercent", 1);
        tabs.Items.Add(new TabItem { Header = "Parametri utensile + supporto", Content = _profiles });
        root.Children.Add(tabs);
        Content = root;
        Closed += (_, _) =>
        {
            Value.Tools.CollectionChanged -= ToolsChanged;
            Value.Materials.CollectionChanged -= MaterialsChanged;
        };
    }

    private static DataGrid Grid<T>(IList<T> items)
    {
        return new DataGrid
        {
            ItemsSource = items,
            AutoGenerateColumns = false,
            CanUserAddRows = true,
            CanUserDeleteRows = true,
            Margin = new Thickness(0, 8, 0, 0),
            HeadersVisibility = DataGridHeadersVisibility.All,
            IsReadOnly = false
        };
    }

    private static void AddColumn(DataGrid grid, string header, string path, double width)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true },
            Width = new DataGridLength(width, DataGridLengthUnitType.Star)
        });
    }

    private static void AddChoiceColumn(DataGrid grid, string header, string path, System.Collections.IEnumerable choices)
    {
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = header,
            ItemsSource = choices,
            DisplayMemberPath = "Name",
            SelectedValuePath = "Id",
            SelectedValueBinding = new Binding(path) { Mode = BindingMode.TwoWay },
            Width = new DataGridLength(1.4, DataGridLengthUnitType.Star)
        });
    }

    private void RemoveProfiles(Predicate<Profile> matches)
    {
        foreach (var profile in Value.Profiles.Where(profile => matches(profile)).ToList())
        {
            Value.Profiles.Remove(profile);
        }
    }

    private void ToolsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tool in (e.NewItems?.OfType<Tool>() ?? Enumerable.Empty<Tool>()).Where(tool => tool.Id == Guid.Empty))
        {
            tool.Id = Guid.NewGuid();
        }
        foreach (var tool in e.OldItems?.OfType<Tool>() ?? Enumerable.Empty<Tool>())
        {
            RemoveProfiles(profile => profile.ToolId == tool.Id);
        }
    }

    private void MaterialsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var material in (e.NewItems?.OfType<Material>() ?? Enumerable.Empty<Material>()).Where(material => material.Id == Guid.Empty))
        {
            material.Id = Guid.NewGuid();
        }
        foreach (var material in e.OldItems?.OfType<Material>() ?? Enumerable.Empty<Material>())
        {
            RemoveProfiles(profile => profile.MaterialId == material.Id);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var grid in new[] { _tools, _materials, _profiles })
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                MessageBox.Show(this, "Correggi i valori non validi nella tabella.", "RonCUT", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var names = StringComparer.OrdinalIgnoreCase;
        bool bad = Value.Tools.Count == 0 || Value.Materials.Count == 0
            || Value.Tools.Any(t => string.IsNullOrWhiteSpace(t.Name) || !double.IsFinite(t.Diameter) || t.Diameter <= 0 || t.Flutes < 1)
            || Value.Materials.Any(m => string.IsNullOrWhiteSpace(m.Name))
            || Value.Tools.Select(t => t.Name.Trim()).Distinct(names).Count() != Value.Tools.Count
            || Value.Materials.Select(m => m.Name.Trim()).Distinct(names).Count() != Value.Materials.Count
            || Value.Profiles.Any(p => !Value.Tools.Any(t => t.Id == p.ToolId) || !Value.Materials.Any(m => m.Id == p.MaterialId)
                || p.Parameters == null || !Valid(p.Parameters.StepDown) || !Valid(p.Parameters.FeedXY) || !Valid(p.Parameters.FeedZ)
                || !Valid(p.Parameters.StepOverPercent) || p.Parameters.StepOverPercent > 100 || !Valid(p.Parameters.SpindleRpm) || p.Parameters.SpindleRpm > 100000)
            || Value.Profiles.GroupBy(p => (p.ToolId, p.MaterialId)).Any(g => g.Count() > 1);
        if (bad)
        {
            MessageBox.Show(this, "Controlla nomi univoci, diametri e parametri positivi. Ogni profilo deve riferirsi a un utensile e a un supporto esistenti e non può essere duplicato.", "Dati non validi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var tool in Value.Tools)
        {
            tool.Name = tool.Name.Trim();
        }
        foreach (var material in Value.Materials)
        {
            material.Name = material.Name.Trim();
        }
        foreach (var profile in Value.Profiles)
        {
            profile.ToolName = null;
            profile.MaterialName = null;
        }
        DialogResult = true;
    }

    private static bool Valid(double x) => double.IsFinite(x) && x > 0;
}
