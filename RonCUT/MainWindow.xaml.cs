using Microsoft.Win32;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RonCUT;

public partial class MainWindow : Window
{
    private const double Ppm = 7.0;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _projectJson = CreateProjectJsonOptions();
    private readonly string _libraryPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RonCUT", "utensili-supporti.json");
    private readonly string _generalSettingsPath;
    private Library _library = Library.CreateDefault();
    private Job _job = new();
    private GeneralSettings _generalSettings = new GeneralSettings();
    private bool _generalSettingsUsable = true;
    private WebcamStreamSettings? _webcamSettings;
    private WebcamSession? _webcam;
    private Task _webcamReleased = Task.CompletedTask;
    private Operation? _selected;
    private readonly HashSet<Operation> _selectedOperations = new HashSet<Operation>();
    private bool _depthMixed;
    private bool _tabHeightMixed;
    private string? _filePath;
    private string _mode = "Seleziona";
    private bool _loading;
    private bool _dirty;
    private bool _panning;
    private bool _measuring;
    private Point _last;
    private Point _measureStart;
    private System.Windows.Shapes.Path? _grid;
    private readonly DispatcherTimer _previewTimer = new DispatcherTimer();
    private readonly HashSet<TextBox> _invalidFields = new HashSet<TextBox>();
    private readonly List<PreviewHitRegion> _previewHits = new List<PreviewHitRegion>();

    private sealed record PreviewHitRegion(Operation Operation, List<Pt> Points, bool Closed, double ToolDiameter);

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? generalSettingsPath)
    {
        _generalSettingsPath = generalSettingsPath ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RonCUT", "impostazioni-generali.json");
        InitializeComponent();
        LoadLibrary();
        LoadGeneralSettings();
        UpdateWebcam();
        _loading = true;
        SelectLibraryDefaults();
        RefreshCombos();
        LoadProfile();
        WriteFields();
        _loading = false;
        _previewTimer.Interval = TimeSpan.FromMilliseconds(150);
        _previewTimer.Tick += PreviewTimer_Tick;
        foreach (var box in new[] { StepDownBox, FeedXYBox, FeedZBox, StepoverBox, SpindleBox, DepthBox, TabHeightBox })
        {
            box.TextChanged += Parameter_TextChanged;
        }
        UpdateToolHighlight();
        Draw();
    }

    private static JsonSerializerOptions CreateProjectJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            string? propertyName = typeInfo.Type == typeof(Job) ? nameof(Job.SafeZ)
                : typeInfo.Type == typeof(Operation) ? nameof(Operation.TabLength) : null;
            if (propertyName == null)
            {
                return;
            }
            var property = typeInfo.Properties.FirstOrDefault(p => p.Name == propertyName);
            if (property != null)
            {
                property.ShouldSerialize = (_, _) => false;
            }
        });
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = resolver
        };
    }

    private void LoadGeneralSettings()
    {
        try
        {
            EnsureDefaultSettingsFile();
            _generalSettings = GeneralSettings.Load(_generalSettingsPath);
            _generalSettings.Apply(_job);
            _generalSettingsUsable = true;
        }
        catch (Exception ex)
        {
            _generalSettingsUsable = false;
            MessageBox.Show(this, "Impossibile leggere le impostazioni generali: " + ex.Message + "\nApri Impostazioni generali e salva valori validi prima di salvare o esportare un progetto.", "RonCUT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateGeneralSummary();
    }

    private void EnsureDefaultSettingsFile()
    {
        if (File.Exists(_generalSettingsPath))
        {
            return;
        }
        string? directory = System.IO.Path.GetDirectoryName(_generalSettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        try
        {
            using var stream = new FileStream(_generalSettingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, new GeneralSettings(), _json);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(_generalSettingsPath))
        {
            // Un'altra istanza ha creato le impostazioni nello stesso momento.
        }
    }

    private void UpdateGeneralSummary()
    {
        GeneralSummary.Text = _generalSettingsUsable
            ? "Generali · Z sicurezza " + N(_generalSettings.SafeZ) + " mm · ponticelli " + N(_generalSettings.TabLength) + " mm"
            : "Impostazioni generali non disponibili: apri e salva valori validi.";
    }

    private void EnsureGeneralSettingsUsable()
    {
        if (!_generalSettingsUsable)
        {
            throw new InvalidOperationException("Apri Impostazioni generali e salva valori validi prima di salvare o esportare un progetto.");
        }
        _generalSettings.Validate();
    }

    private void ApplyGeneralSettings(GeneralSettings settings)
    {
        settings.Validate();
        string? directory = System.IO.Path.GetDirectoryName(_generalSettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        AtomicWrite(_generalSettingsPath, JsonSerializer.Serialize(settings, _json));
        _generalSettings = Clone(settings);
        _generalSettingsUsable = true;
        _generalSettings.Apply(_job);
        UpdateGeneralSummary();
        UpdateWebcam();
        StatusText.Text = "Impostazioni generali salvate per tutti i progetti.";
        Draw();
    }

    private void GeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncFields();
            var dialog = new GeneralSettingsWindow(_generalSettings, _webcam)
            {
                Owner = this
            };
            bool saved = dialog.ShowDialog() == true;
            _webcamReleased = Task.WhenAll(_webcamReleased, dialog.WebcamReleased);
            if (saved)
            {
                ApplyGeneralSettings(dialog.Value);
            }
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void UpdateWebcam()
    {
        var desired = _generalSettings.GetWebcamStream();
        if (desired == _webcamSettings)
        {
            return;
        }
        StopWebcam();
        _webcamSettings = desired;
        if (desired != null)
        {
            _webcam = new WebcamSession(desired.Device, desired, _webcamReleased);
        }
    }

    private void StopWebcam()
    {
        if (_webcam != null)
        {
            _webcamReleased = Task.WhenAll(_webcamReleased, _webcam.Stop());
            _webcam = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopWebcam();
        base.OnClosed(e);
    }

    private void LoadLibrary()
    {
        try
        {
            if (File.Exists(_libraryPath))
            {
                _library = Library.Read(File.ReadAllText(_libraryPath));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossibile leggere l'archivio utensili: " + ex.Message + "\nVerrà usato quello predefinito.", "RonCUT", MessageBoxButton.OK, MessageBoxImage.Warning);
            _library = Library.CreateDefault();
        }
    }

    private void SaveLibrary(Library library)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_libraryPath)!);
        AtomicWrite(_libraryPath, JsonSerializer.Serialize(library, _json));
        _library = library;
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private void RefreshCombos()
    {
        var tool = _library.FindTool(_job.Tool);
        var material = _library.FindMaterial(_job.Material.Id, _job.Material.Name);
        ToolCombo.ItemsSource = _library.Tools;
        MaterialCombo.ItemsSource = _library.Materials;
        ToolCombo.SelectedItem = tool != null && Library.SameToolGeometry(tool, _job.Tool) ? tool : null;
        MaterialCombo.SelectedItem = material;
        SaveProfileButton.IsEnabled = ToolCombo.SelectedItem != null && material != null;
    }

    private void SelectLibraryDefaults()
    {
        _job.Tool = Clone(_library.FindTool(_job.Tool) ?? _library.Tools[0]);
        _job.Material = Clone(_library.FindMaterial(_job.Material.Id, _job.Material.Name) ?? _library.Materials[0]);
    }

    private bool UpdateProjectLibraryNames()
    {
        bool renamed = BindToolName(_job.Tool);
        var material = _library.FindMaterial(_job.Material.Id, _job.Material.Name);
        if (material != null)
        {
            renamed |= _job.Material.Name != material.Name;
            _job.Material.Id = material.Id;
            _job.Material.Name = material.Name;
        }
        foreach (var change in _job.Operations.Where(operation => operation.IsToolChange).Select(operation => operation.NextTool).OfType<ToolChange>())
        {
            if (change.Tool != null)
            {
                renamed |= BindToolName(change.Tool);
            }
            var changeMaterial = _library.FindMaterial(change.MaterialId, change.MaterialName);
            if (changeMaterial != null)
            {
                renamed |= change.MaterialName != changeMaterial.Name;
                change.MaterialId = changeMaterial.Id;
                change.MaterialName = changeMaterial.Name;
            }
        }
        return renamed;
    }

    private bool BindToolName(Tool saved)
    {
        var catalog = _library.FindTool(saved);
        if (catalog == null)
        {
            return false;
        }
        bool renamed = saved.Name != catalog.Name;
        saved.Id = catalog.Id;
        saved.Name = catalog.Name;
        return renamed;
    }

    private void ShowProjectProfile()
    {
        ProfileLabel.Text = "Valori del progetto · " + _job.Tool.DisplayName + " / " + _job.Material.Name;
        if (!SaveProfileButton.IsEnabled)
        {
            ProfileLabel.Text += "\nLa coppia salvata differisce dall’archivio. I valori del progetto restano attivi finché non scegli una nuova coppia.";
        }
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ToolCombo.SelectedItem is not Tool || MaterialCombo.SelectedItem is not Material)
        {
            return;
        }
        try
        {
            SyncFields();
        }
        catch (Exception ex)
        {
            _loading = true;
            RefreshCombos();
            _loading = false;
            Error(ex);
            return;
        }
        LoadProfile();
        WriteFields();
        _dirty = true;
        Draw();
    }

    private void LoadProfile()
    {
        if (ToolCombo.SelectedItem is not Tool tool || MaterialCombo.SelectedItem is not Material material)
        {
            return;
        }
        _job.Tool = Clone(tool);
        _job.Material = Clone(material);
        var profile = _library.FindProfile(tool.Id, material.Id);
        SaveProfileButton.IsEnabled = true;
        _job.ProfileConfirmed = profile != null;
        _job.Parameters = profile != null ? Clone(profile.Parameters) : new CuttingParameters();
        ProfileLabel.Text = profile != null
            ? "Profilo salvato · fresa Ø " + N(tool.Diameter) + " mm"
            : "Profilo non ancora salvato: verifica i valori e salva questa coppia prima di esportare.";
    }

    private void WriteFields()
    {
        bool prior = _loading;
        _loading = true;
        ResetInputValidation();
        StepDownBox.Text = N(_job.Parameters.StepDown);
        FeedXYBox.Text = N(_job.Parameters.FeedXY);
        FeedZBox.Text = N(_job.Parameters.FeedZ);
        StepoverBox.Text = N(_job.Parameters.StepOverPercent);
        SpindleBox.Text = N(_job.Parameters.SpindleRpm);
        var selection = SelectedOperations();
        var selected = selection.Where(operation => !operation.IsToolChange).ToList();
        var cuts = selected.Where(SupportsTabs).ToList();
        _depthMixed = selected.Select(op => op.Depth).Distinct().Skip(1).Any();
        _tabHeightMixed = cuts.Select(op => op.TabHeight).Distinct().Skip(1).Any();
        TabHeightBox.IsEnabled = cuts.Count > 0;
        EditToolChangeButton.Visibility = selection.Count == 1 && selection[0].IsToolChange ? Visibility.Visible : Visibility.Collapsed;
        DeleteOperationsButton.Visibility = selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchHint.Text = selected.Count > 1 ? "I valori diversi sono mostrati vuoti. Compila soltanto i campi da uniformare alle selezionate." : "";
        if (selected.Count > 1)
        {
            OperationDetails.Visibility = Visibility.Visible;
            DepthBox.Text = _depthMixed ? "" : N(selected[0].Depth);
            TabHeightBox.Text = cuts.Count == 0 || _tabHeightMixed ? "" : N(cuts[0].TabHeight);
            SelectedLabel.Text = selected.Count + " lavorazioni selezionate · " + cuts.Count + " tagli";
        }
        else if (selected.Count == 1)
        {
            var operation = selected[0];
            OperationDetails.Visibility = Visibility.Visible;
            DepthBox.Text = N(operation.Depth);
            TabHeightBox.Text = N(operation.TabHeight);
            SelectedLabel.Text = operation.Kind + " · " + operation.Tabs.Count + " ponticelli";
            if (operation.Kind == "Tasca")
            {
                SelectedLabel.Text = "Tasca · " + (operation.IslandContourIds?.Count ?? 0) + " isole";
            }
            else if (operation.Kind == "Foro")
            {
                var contour = _job.Contours.FirstOrDefault(c => c.Id == operation.ContourId);
                if (contour != null)
                {
                    double diameter = Cam.CircleInfo(contour).Diameter;
                    SelectedLabel.Text = "Foro · Ø effettivo " + MeasureNumber(Math.Max(diameter, Cam.ToolFor(_job, operation).Diameter)) + " mm";
                }
            }
        }
        else
        {
            OperationDetails.Visibility = Visibility.Collapsed;
            DepthBox.Text = "";
            TabHeightBox.Text = "";
            SelectedLabel.Text = selection.Count == 1
                ? Operation.ToolChangeKind + "\nRipresa con «" + (selection[0].NextTool?.Tool?.DisplayName ?? "Da scegliere") + "»"
                : selection.Count > 1 ? selection.Count + " pause selezionate" : "Nessuna lavorazione selezionata";
        }
        _loading = prior;
    }

    private static string N(double n) => n.ToString("G", CultureInfo.CurrentCulture);

    private static bool SupportsTabs(Operation op) => op.Kind is "Taglio esterno" or "Taglio interno";

    private List<Operation> SelectedOperations()
    {
        if (_selected == null || !_job.Operations.Contains(_selected))
        {
            return new List<Operation>();
        }
        if (!_selectedOperations.Contains(_selected))
        {
            return new List<Operation> { _selected };
        }
        return _job.Operations.Where(_selectedOperations.Contains).ToList();
    }

    private void SelectOnly(Operation? operation)
    {
        _selected = operation;
        _selectedOperations.Clear();
        if (operation != null)
        {
            _selectedOperations.Add(operation);
        }
    }
    private static string MeasureNumber(double n) => n.ToString("0.0", CultureInfo.CurrentCulture);

    private static double Read(TextBox box, string name)
    {
        string s = box.Text.Trim().Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n))
        {
            throw new InvalidOperationException("Valore non valido: " + name);
        }
        return n;
    }

    private static double ReadPositive(TextBox box, string name, double max)
    {
        double value = Read(box, name);
        if (value <= 0 || value > max)
        {
            throw new InvalidOperationException(name + " deve essere maggiore di zero e non superare " + N(max) + ".");
        }
        return value;
    }

    private void SyncFields()
    {
        double step = ReadPositive(StepDownBox, "Avanzamento Z per passata", 1000);
        double feedXY = ReadPositive(FeedXYBox, "Avanzamento XY", 100000);
        double feedZ = ReadPositive(FeedZBox, "Velocità discesa Z", 100000);
        double stepover = ReadPositive(StepoverBox, "Passo laterale", 100);
        double spindle = ReadPositive(SpindleBox, "Giri mandrino", 100000);
        SyncOperation();
        _job.Parameters.StepDown = step;
        _job.Parameters.FeedXY = feedXY;
        _job.Parameters.FeedZ = feedZ;
        _job.Parameters.StepOverPercent = stepover;
        _job.Parameters.SpindleRpm = spindle;
    }

    private void SyncOperation()
    {
        var selected = SelectedOperations().Where(operation => !operation.IsToolChange).ToList();
        if (selected.Count == 0)
        {
            return;
        }
        var cuts = selected.Where(SupportsTabs).ToList();
        double? depth = !_depthMixed || _invalidFields.Contains(DepthBox)
            ? ReadPositive(DepthBox, "Profondità totale", 1000) : null;
        double? height = cuts.Count > 0 && (!_tabHeightMixed || _invalidFields.Contains(TabHeightBox))
            ? ReadPositive(TabHeightBox, "Altezza ponticello", 1000) : null;
        foreach (var op in selected)
        {
            if (depth.HasValue)
            {
                op.Depth = depth.Value;
            }
            if (height.HasValue && SupportsTabs(op))
            {
                op.TabHeight = height.Value;
            }
        }
    }

    private void Parameter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox box)
        {
            return;
        }
        if ((box == DepthBox || box == TabHeightBox) && _selected == null)
        {
            return;
        }
        _dirty = true;
        try
        {
            if (box == DepthBox)
            {
                double depth = ReadPositive(box, "Profondità totale", 1000);
                foreach (var op in SelectedOperations().Where(operation => !operation.IsToolChange))
                {
                    op.Depth = depth;
                }
                _depthMixed = false;
                UpdateOperationLabels();
            }
            else if (box == TabHeightBox)
            {
                double height = ReadPositive(box, "Altezza ponticello", 1000);
                foreach (var op in SelectedOperations().Where(SupportsTabs))
                {
                    op.TabHeight = height;
                }
                _tabHeightMixed = false;
            }
            else if (box == StepDownBox)
            {
                _job.Parameters.StepDown = ReadPositive(box, "Avanzamento Z per passata", 1000);
            }
            else if (box == FeedXYBox)
            {
                _job.Parameters.FeedXY = ReadPositive(box, "Avanzamento XY", 100000);
            }
            else if (box == FeedZBox)
            {
                _job.Parameters.FeedZ = ReadPositive(box, "Velocità discesa Z", 100000);
            }
            else if (box == StepoverBox)
            {
                _job.Parameters.StepOverPercent = ReadPositive(box, "Passo laterale", 100);
            }
            else if (box == SpindleBox)
            {
                _job.Parameters.SpindleRpm = ReadPositive(box, "Giri mandrino", 100000);
            }
            _invalidFields.Remove(box);
            box.ClearValue(BorderBrushProperty);
            box.ToolTip = null;
            if (_invalidFields.Count == 0)
            {
                StatusText.Text = "Parametri aggiornati automaticamente.";
                _previewTimer.Stop();
                _previewTimer.Start();
            }
        }
        catch (InvalidOperationException ex)
        {
            _invalidFields.Add(box);
            box.BorderBrush = Brushes.IndianRed;
            box.ToolTip = ex.Message;
            _previewTimer.Stop();
            StatusText.Text = "Valore non valido: correggi il campo evidenziato.";
        }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        if (_invalidFields.Count == 0)
        {
            Draw();
        }
    }

    private void ResetInputValidation()
    {
        foreach (var box in _invalidFields)
        {
            box.ClearValue(BorderBrushProperty);
            box.ToolTip = null;
        }
        _invalidFields.Clear();
    }

    private void Library_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new LibraryWindow(_library) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                SaveLibrary(dialog.Value);
                bool hasProject = !string.IsNullOrEmpty(_job.SvgText);
                if (!hasProject)
                {
                    SelectLibraryDefaults();
                }
                else
                {
                    _dirty |= UpdateProjectLibraryNames();
                }
                _loading = true;
                RefreshCombos();
                _loading = false;
                if (hasProject)
                {
                    RefreshOperations();
                    ShowProjectProfile();
                }
                else
                {
                    LoadProfile();
                }
                WriteFields();
                Draw();
            }
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncFields();
            if (_job.Parameters.StepDown <= 0 || _job.Parameters.FeedXY <= 0 || _job.Parameters.FeedZ <= 0 || _job.Parameters.StepOverPercent <= 0 || _job.Parameters.StepOverPercent > 100)
            {
                throw new InvalidOperationException("Inserisci parametri positivi e un passo laterale compreso tra 0 e 100%.");
            }
            if (ToolCombo.SelectedItem is not Tool tool || MaterialCombo.SelectedItem is not Material material)
            {
                throw new InvalidOperationException("Scegli utensile e supporto dall’archivio prima di salvare il profilo.");
            }
            var updated = Clone(_library);
            var existing = updated.FindProfile(tool.Id, material.Id);
            if (existing == null)
            {
                existing = new Profile { ToolId = tool.Id, MaterialId = material.Id };
                updated.Profiles.Add(existing);
            }
            existing.Parameters = Clone(_job.Parameters);
            SaveLibrary(updated);
            _loading = true;
            RefreshCombos();
            _loading = false;
            _job.ProfileConfirmed = true;
            ProfileLabel.Text = "Profilo salvato · fresa Ø " + N(_job.Tool.Diameter) + " mm";
            StatusText.Text = "Parametri salvati per " + _job.Tool.Name + " / " + _job.Material.Name;
            _dirty = true;
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }
        var dialog = new OpenFileDialog { Filter = "File SVG (*.svg)|*.svg", Title = "Importa SVG" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            string svg = File.ReadAllText(dialog.FileName);
            var choices = new[] { "Punti tipografici: 1 unità = 1 pt (72 per pollice)", "Pixel SVG: 1 unità = 1 px (96 per pollice)", "Millimetri: 1 unità = 1 mm", "Pollici: 1 unità = 25,4 mm" };
            int selected = Choose("Scala del disegno SVG", "Scegli il significato delle unità numeriche del disegno. Controlla poi le dimensioni indicate in alto; se non coincidono, reimporta con un'altra scala.", choices, 0);
            if (selected < 0)
            {
                return;
            }
            double unit = new[] { 25.4 / 72, 25.4 / 96, 1.0, 25.4 }[selected];
            var contours = SvgImporter.Parse(svg, unit);
            _job.SvgText = svg;
            _job.SvgName = System.IO.Path.GetFileName(dialog.FileName);
            _job.SvgUnitMm = unit;
            _job.Contours = contours;
            _job.Operations.Clear();
            if (_generalSettingsUsable)
            {
                _generalSettings.Apply(_job);
            }
            SelectOnly(null);
            _job.ZeroX = contours.SelectMany(c => c.Points).Min(p => p.X);
            _job.ZeroY = contours.SelectMany(c => c.Points).Max(p => p.Y);
            _filePath = null;
            _dirty = true;
            RefreshOperations();
            WriteFields();
            UpdateLabels();
            Fit();
            Draw();
            StatusText.Text = "SVG importato: " + contours.Count + " contorni geometrici. Testi, immagini ed effetti grafici ignorati. Verifica dimensioni e zero.";
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private int Choose(string title, string prompt, string[] choices, int initial, string action = "Importa")
    {
        var dialog = new Window { Title = title, Owner = this, Width = 540, Height = 235, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var combo = new ComboBox { ItemsSource = choices, SelectedIndex = initial, Height = 34 };
        panel.Children.Add(combo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = action, IsDefault = true, Padding = new Thickness(16, 5, 16, 5) };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? combo.SelectedIndex : -1;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }
        var dialog = new OpenFileDialog { Filter = "Progetto RonCUT (*.roncut)|*.roncut" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            LoadProject(dialog.FileName);
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void LoadProject(string path)
    {
        var loaded = JsonSerializer.Deserialize<Job>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Progetto vuoto.");
        if (loaded.Version is not (1 or 2) || loaded.Contours == null || loaded.Operations == null || loaded.Tool == null || loaded.Material == null || loaded.Parameters == null || string.IsNullOrWhiteSpace(loaded.SvgText))
        {
            throw new InvalidOperationException("Progetto non valido o di una versione non supportata.");
        }
        if (_generalSettingsUsable)
        {
            _generalSettings.Apply(loaded);
        }
        foreach (var operation in loaded.Operations)
        {
            operation.IslandContourIds ??= new List<string>();
        }
        _job = loaded;
        UpdateProjectLibraryNames();
        SelectOnly(null);
        _filePath = path;
        _loading = true;
        RefreshCombos();
        RefreshOperations();
        ShowProjectProfile();
        WriteFields();
        _loading = false;
        _dirty = false;
        UpdateLabels();
        Fit();
        StatusText.Text = "Progetto aperto. Z di sicurezza e lunghezza ponticelli usano le impostazioni generali.";
        Draw();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Save(false))
        {
            LargeMessageWindow.Inform(this, "Progetto salvato", "Il progetto è stato salvato correttamente.\n\n" + _filePath);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (Save(true))
        {
            LargeMessageWindow.Inform(this, "Progetto salvato", "Il progetto è stato salvato correttamente.\n\n" + _filePath);
        }
    }

    private bool Save(bool asNew)
    {
        try
        {
            if (string.IsNullOrEmpty(_job.SvgText))
            {
                throw new InvalidOperationException("Importa un SVG prima di salvare un progetto.");
            }
            EnsureGeneralSettingsUsable();
            SyncFields();
            _generalSettings.Apply(_job);
            string? destination = _filePath;
            if (asNew || string.IsNullOrEmpty(destination))
            {
                var dialog = new SaveFileDialog { Filter = "Progetto RonCUT (*.roncut)|*.roncut", DefaultExt = ".roncut", AddExtension = true, FileName = System.IO.Path.GetFileNameWithoutExtension(_job.SvgName) };
                if (dialog.ShowDialog(this) != true)
                {
                    return false;
                }
                destination = dialog.FileName;
            }
            _job.Version = _job.Operations.Any(operation => operation.IsToolChange) ? 2 : 1;
            AtomicWrite(destination!, JsonSerializer.Serialize(_job, _projectJson));
            _filePath = destination;
            _dirty = false;
            StatusText.Text = "Progetto salvato: " + destination;
            return true;
        }
        catch (Exception ex)
        {
            Error(ex);
            return false;
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
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

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureGeneralSettingsUsable();
            SyncFields();
            _generalSettings.Apply(_job);
            if (!_job.ProfileConfirmed)
            {
                throw new InvalidOperationException("Salva prima un profilo verificato per l'utensile e il supporto selezionati.");
            }
            string code = Cam.Generate(_job);
            var dialog = new SaveFileDialog { Filter = "G-code (*.nc)|*.nc|G-code (*.gcode)|*.gcode", DefaultExt = ".nc", AddExtension = true, FileName = System.IO.Path.GetFileNameWithoutExtension(_job.SvgName) };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            AtomicWrite(dialog.FileName, code);
            StatusText.Text = "G-code esportato: " + dialog.FileName;
            LargeMessageWindow.Inform(this, "G-code esportato", "File salvato: " + dialog.FileName + "\n\nAprilo in Candle e controlla dimensioni, zero, profondità, ponticelli e percorsi prima di qualsiasi lavorazione.");
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void Cut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureGeneralSettingsUsable();
            SyncFields();
            _generalSettings.Apply(_job);
            if (_job.Operations.Count > 0 && !_job.ProfileConfirmed)
            {
                throw new InvalidOperationException("Salva prima un profilo verificato per l'utensile e il supporto selezionati.");
            }
            var window = new CutWindow(Cam.CompilePhases(_job), generalSettings: Clone(_generalSettings)) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void CutSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = CutSettings.DefaultPath;
            var window = new CutSettingsWindow(CutSettings.Load(path), path) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty)
        {
            return true;
        }
        var result = LargeMessageWindow.AskSave(this);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && Save(false));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string mode)
        {
            return;
        }
        try
        {
            SyncFields();
            _mode = mode;
            UpdateToolHighlight();
            ModeLabel.Text = mode.ToUpperInvariant();
            StatusText.Text = mode switch
            {
                "Punto zero" => "Fai clic sul disegno per impostare lo zero XY. Lo zero Z è la superficie del materiale.",
                "Ponticelli" => "Seleziona un taglio e fai clic vicino al contorno per aggiungere ponticelli.",
                "Isole" => "Seleziona una tasca, poi fai clic su un contorno interno per aggiungere o rimuovere un'isola.",
                "Seleziona" => "Fai clic su una lavorazione per selezionarla; sul vuoto per deselezionare.",
                _ => "Fai clic su un contorno per aggiungere: " + mode
            };
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void UpdateToolHighlight()
    {
        foreach (var button in ToolPanel.Children.OfType<Button>())
        {
            bool active = button.Tag is string mode && mode == _mode;
            button.Background = active ? new SolidColorBrush(Color.FromRgb(211, 233, 255)) : Brushes.White;
            button.BorderBrush = active ? new SolidColorBrush(Color.FromRgb(31, 111, 187)) : new SolidColorBrush(Color.FromRgb(203, 217, 231));
            button.BorderThickness = new Thickness(active ? 2 : 1);
            button.ToolTip = active ? "Strumento attivo" : null;
        }
    }

    private void Operation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        try
        {
            SyncFields();
        }
        catch (Exception ex)
        {
            _loading = true;
            var previous = SelectedOperations().ToHashSet();
            foreach (var item in OperationsList.Items.OfType<ListBoxItem>())
            {
                item.IsSelected = item.Tag is Operation op && previous.Contains(op);
            }
            _loading = false;
            Error(ex);
            return;
        }
        var selection = OperationsList.SelectedItems.OfType<ListBoxItem>().Select(item => (Operation)item.Tag).ToList();
        _selectedOperations.Clear();
        _selectedOperations.UnionWith(selection);
        _selected = e.AddedItems.OfType<ListBoxItem>().Select(item => item.Tag as Operation).LastOrDefault() ?? selection.FirstOrDefault();
        WriteFields();
        Draw();
    }

    private void RefreshOperations()
    {
        var selected = SelectedOperations().ToHashSet();
        _selectedOperations.Clear();
        _selectedOperations.UnionWith(selected);
        bool prior = _loading;
        _loading = true;
        OperationsList.Items.Clear();
        for (int i = 0; i < _job.Operations.Count; i++)
        {
            var op = _job.Operations[i];
            var item = new ListBoxItem { Tag = op };
            OperationsList.Items.Add(item);
            if (selected.Contains(op))
            {
                item.IsSelected = true;
            }
        }
        UpdateOperationLabels();
        _loading = prior;
    }

    private void SelectAllOperations_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SyncFields();
            OperationsList.SelectAll();
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void UpdateOperationLabels()
    {
        for (int i = 0; i < OperationsList.Items.Count; i++)
        {
            if (OperationsList.Items[i] is not ListBoxItem item || item.Tag is not Operation op)
            {
                continue;
            }
            if (op.IsToolChange)
            {
                var label = op.NextTool?.Tool?.DisplayName ?? "Utensile da scegliere";
                item.Content = new TextBlock
                {
                    Text = (i + 1) + ". Pausa cambio utensile\n     → " + label,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                item.ToolTip = label;
                continue;
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(DepthColor(op.Depth)),
                Margin = new Thickness(0, 0, 7, 0)
            });
            content.Children.Add(new TextBlock { Text = (i + 1) + ". " + op.Kind + " · " + MeasureNumber(op.Depth) + " mm" });
            item.Content = content;
            item.ToolTip = "Profondità esatta: " + N(op.Depth) + " mm";
        }
    }

    private Color DepthColor(double depth)
    {
        Color[] palette =
        {
            Color.FromRgb(30, 119, 201), Color.FromRgb(210, 48, 68),
            Color.FromRgb(32, 143, 106), Color.FromRgb(135, 76, 182),
            Color.FromRgb(211, 111, 32), Color.FromRgb(21, 143, 160),
            Color.FromRgb(190, 55, 139), Color.FromRgb(107, 126, 32)
        };
        var depths = _job.Operations.Where(o => !o.IsToolChange).Select(o => o.Depth).Distinct().OrderBy(d => d).ToList();
        int index = Math.Max(0, depths.IndexOf(depth));
        if (index < palette.Length)
        {
            return palette[index];
        }
        double hue = (index * 137.508) % 360;
        double chroma = 0.58, m = 0.20;
        double x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
        {
            return;
        }
        var selected = SelectedOperations().ToHashSet();
        _job.Operations.RemoveAll(selected.Contains);
        SelectOnly(null);
        _dirty = true;
        RefreshOperations();
        WriteFields();
        Draw();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _selected == null || Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox)
        {
            return;
        }
        Delete_Click(sender, e);
        e.Handled = true;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveOperation(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveOperation(1);

    private void MoveOperation(int direction)
    {
        if (_selected == null)
        {
            return;
        }
        try
        {
            SyncFields();
            var selected = SelectedOperations().ToHashSet();
            var ordered = direction < 0 ? selected.OrderBy(_job.Operations.IndexOf).ToList() : selected.OrderByDescending(_job.Operations.IndexOf).ToList();
            bool moved = false;
            foreach (var operation in ordered)
            {
                int oldIndex = _job.Operations.IndexOf(operation);
                int newIndex = oldIndex + direction;
                if (newIndex < 0 || newIndex >= _job.Operations.Count || selected.Contains(_job.Operations[newIndex]))
                {
                    continue;
                }
                _job.Operations.RemoveAt(oldIndex);
                _job.Operations.Insert(newIndex, operation);
                moved = true;
            }
            if (!moved)
            {
                return;
            }
            _dirty = true;
            RefreshOperations();
            Draw();
            StatusText.Text = "Ordine aggiornato: sarà rispettato nel G-code.";
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void UpdateLabels()
    {
        FileLabel.Text = string.IsNullOrEmpty(_job.SvgName) ? "Nessun SVG importato" : _job.SvgName;
        var pts = _job.Contours.SelectMany(c => c.Points).ToList();
        SizeLabel.Text = pts.Count == 0 ? "Unità: mm · griglia: 10 mm"
            : "Disegno: " + MeasureNumber(pts.Max(p => p.X) - pts.Min(p => p.X)) + " × " + MeasureNumber(pts.Max(p => p.Y) - pts.Min(p => p.Y)) + " mm   ·   griglia 10 mm";
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Point p = e.GetPosition(Viewport);
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true;
            _last = p;
            Viewport.CaptureMouse();
            Viewport.Cursor = Cursors.SizeAll;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            _measuring = true;
            _measureStart = p;
            Viewport.CaptureMouse();
            Viewport.Cursor = Cursors.Cross;
            MeasureLine.Visibility = Visibility.Visible;
            MeasureLabel.Visibility = Visibility.Visible;
            Measure(p);
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            Keyboard.Focus(Viewport);
            HandleClick(ToWorld(p));
        }
    }

    private Pt ToWorld(Point p) => new((p.X - Translate.X) / Scale.ScaleX / Ppm, (p.Y - Translate.Y) / Scale.ScaleY / Ppm);

    private void HandleClick(Pt point)
    {
        try
        {
            if (_job.Contours.Count == 0)
            {
                if (_mode == "Seleziona")
                {
                    SelectOnly(null);
                    RefreshOperations();
                    WriteFields();
                    Draw();
                    return;
                }
                throw new InvalidOperationException("Importa prima un SVG.");
            }
            SyncFields();
            if (_mode == "Seleziona")
            {
                if (_previewTimer.IsEnabled)
                {
                    _previewTimer.Stop();
                    Draw();
                }
                SelectPreviewOperation(point);
                return;
            }
            if (_mode == "Punto zero")
            {
                var nearest = _job.Contours.SelectMany(c => c.Points)
                    .Select(p => (Point: p, Distance: Math.Sqrt(Math.Pow(p.X - point.X, 2) + Math.Pow(p.Y - point.Y, 2))))
                    .OrderBy(x => x.Distance).First();
                var zero = nearest.Distance <= 10 / (Ppm * Scale.ScaleX) ? nearest.Point : point;
                _job.ZeroX = zero.X;
                _job.ZeroY = zero.Y;
                _dirty = true;
                Draw();
                StatusText.Text = "Zero XY impostato a X " + MeasureNumber(zero.X) + ", Y " + MeasureNumber(zero.Y) + " nel disegno. In Z lo zero è la superficie del materiale.";
                return;
            }
            if (_mode == "Ponticelli")
            {
                if (SelectedOperations().Count != 1 || _selected == null || !SupportsTabs(_selected))
                {
                    throw new InvalidOperationException("Seleziona prima una lavorazione di taglio interno o esterno.");
                }
                var path = Cam.Build(_job).First(x => x.OperationId == _selected.Id);
                var snapped = NearestPoint(path.Points, true, point).Point;
                double distance = NearestPoint(path.Points, true, point).Distance;
                if (distance > Math.Max(4, 12 / (Ppm * Scale.ScaleX)))
                {
                    throw new InvalidOperationException("Fai clic vicino al percorso del taglio.");
                }
                _selected.Tabs.Add(snapped);
                try
                {
                    Cam.Build(_job);
                }
                catch
                {
                    _selected.Tabs.RemoveAt(_selected.Tabs.Count - 1);
                    throw;
                }
                _dirty = true;
                WriteFields();
                Draw();
                StatusText.Text = "Ponticello aggiunto.";
                return;
            }
            if (_mode == "Isole")
            {
                if (SelectedOperations().Count != 1 || _selected == null || _selected.Kind != "Tasca")
                {
                    throw new InvalidOperationException("Seleziona prima una lavorazione di tipo tasca.");
                }
                var candidate = _job.Contours.Where(c => c.Closed && c.Id != _selected.ContourId)
                    .Select(c => (Contour: c, Hit: NearestPoint(c.Points, true, point)))
                    .OrderBy(x => x.Hit.Distance).FirstOrDefault();
                if (candidate.Contour == null || candidate.Hit.Distance > Math.Max(1, 12 / (Ppm * Scale.ScaleX)))
                {
                    StatusText.Text = "Fai clic vicino a un contorno chiuso interno alla tasca.";
                    return;
                }
                var ids = _selected.IslandContourIds ??= new List<string>();
                var oldIds = ids.ToList();
                bool removing = ids.Contains(candidate.Contour.Id);
                if (removing)
                {
                    ids.Remove(candidate.Contour.Id);
                }
                else
                {
                    ids.Add(candidate.Contour.Id);
                }
                try
                {
                    Cam.Build(_job);
                }
                catch
                {
                    _selected.IslandContourIds = oldIds;
                    throw;
                }
                _dirty = true;
                WriteFields();
                Draw();
                StatusText.Text = removing ? "Isola rimossa: quest'area verrà lavorata." : "Isola aggiunta: quest'area rimarrà intatta.";
                return;
            }
            var hit = _job.Contours.Select(c => (Contour: c, Hit: NearestPoint(c.Points, c.Closed, point))).OrderBy(x => x.Hit.Distance).First();
            if (hit.Hit.Distance > Math.Max(1, 12 / (Ppm * Scale.ScaleX)))
            {
                StatusText.Text = "Fai clic più vicino a un contorno.";
                return;
            }
            var op = new Operation { ContourId = hit.Contour.Id, Kind = _mode, Depth = 3, TabLength = _generalSettings.TabLength, TabHeight = 1 };
            if (op.Kind == "Tasca")
            {
                op.IslandContourIds = PocketGeometry.SuggestIslands(_job, hit.Contour);
            }
            _job.Operations.Add(op);
            try
            {
                Cam.Build(_job);
            }
            catch
            {
                _job.Operations.Remove(op);
                throw;
            }
            SelectOnly(op);
            _dirty = true;
            RefreshOperations();
            WriteFields();
            Draw();
            StatusText.Text = op.Kind == "Tasca" && op.IslandContourIds.Count > 0
                ? "Tasca aggiunta: " + op.IslandContourIds.Count + " isole riconosciute. Verificale con lo strumento Isole."
                : "Lavorazione aggiunta. Imposta la profondità: le modifiche sono immediate.";
        }
        catch (Exception ex)
        {
            Error(ex);
        }
    }

    private void SelectPreviewOperation(Pt point)
    {
        double screenTolerance = 3 / (Ppm * Scale.ScaleX);
        var hit = _previewHits
            .Select((region, index) => (Region: region, Index: index, Distance: NearestPoint(region.Points, region.Closed, point).Distance))
            .Where(candidate => candidate.Distance <= Math.Max(2 / Ppm, candidate.Region.ToolDiameter) / 2 + screenTolerance)
            .OrderBy(candidate => candidate.Distance)
            .ThenByDescending(candidate => candidate.Index)
            .FirstOrDefault();
        SetPreviewSelection(hit.Region?.Operation, Keyboard.Modifiers);
    }

    private void SetPreviewSelection(Operation? operation, ModifierKeys modifiers)
    {
        if (operation == null || (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            SelectOnly(operation);
        }
        else
        {
            var selected = SelectedOperations().ToHashSet();
            if ((modifiers & ModifierKeys.Control) != 0 && selected.Contains(operation))
            {
                selected.Remove(operation);
            }
            else
            {
                selected.Add(operation);
            }
            _selectedOperations.Clear();
            _selectedOperations.UnionWith(selected);
            _selected = selected.Contains(operation) ? operation : selected.FirstOrDefault();
        }
        RefreshOperations();
        WriteFields();
        Draw();
        StatusText.Text = _selected == null ? "Nessuna lavorazione selezionata." : SelectedOperations().Count + " lavorazioni selezionate.";
    }

    private static (Pt Point, double Distance) NearestPoint(List<Pt> pts, bool closed, Pt target)
    {
        if (pts.Count == 1)
        {
            var p = pts[0];
            return (p, Math.Sqrt(Math.Pow(p.X - target.X, 2) + Math.Pow(p.Y - target.Y, 2)));
        }
        var best = new Pt();
        double distance = double.MaxValue;
        int count = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            if (len2 < 1e-16)
            {
                continue;
            }
            double t = Math.Clamp(((target.X - a.X) * dx + (target.Y - a.Y) * dy) / len2, 0, 1);
            var p = new Pt(a.X + t * dx, a.Y + t * dy);
            double d = Math.Sqrt(Math.Pow(p.X - target.X, 2) + Math.Pow(p.Y - target.Y, 2));
            if (d < distance)
            {
                best = p;
                distance = d;
            }
        }
        return (best, distance);
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        Point current = e.GetPosition(Viewport);
        if (_panning)
        {
            Translate.X += current.X - _last.X;
            Translate.Y += current.Y - _last.Y;
            _last = current;
            UpdateGrid();
        }
        else if (_measuring)
        {
            Measure(current);
        }
    }

    private void Measure(Point p)
    {
        MeasureLine.X1 = _measureStart.X;
        MeasureLine.Y1 = _measureStart.Y;
        MeasureLine.X2 = p.X;
        MeasureLine.Y2 = p.Y;
        double mm = (p - _measureStart).Length / Scale.ScaleX / Ppm;
        MeasureText.Text = MeasureNumber(mm) + " mm";
        Canvas.SetLeft(MeasureLabel, p.X + 12);
        Canvas.SetTop(MeasureLabel, p.Y + 12);
        StatusText.Text = "Misura: " + MeasureNumber(mm) + " mm";
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((_panning && e.ChangedButton == MouseButton.Middle) || (_measuring && e.ChangedButton == MouseButton.Right))
        {
            _panning = false;
            _measuring = false;
            Viewport.ReleaseMouseCapture();
            Viewport.Cursor = Cursors.Arrow;
            MeasureLine.Visibility = Visibility.Collapsed;
            MeasureLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point p = e.GetPosition(Viewport);
        double old = Scale.ScaleX, next = Math.Clamp(old * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.02, 20);
        double x = (p.X - Translate.X) / old, y = (p.Y - Translate.Y) / old;
        Scale.ScaleX = next;
        Scale.ScaleY = next;
        Translate.X = p.X - x * next;
        Translate.Y = p.Y - y * next;
        UpdateGrid();
        e.Handled = true;
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Scale.ScaleX == 1 && Translate.X == 0 && Translate.Y == 0)
        {
            Fit();
        }
        UpdateGrid();
    }

    private void Fit()
    {
        var pts = _job.Contours.SelectMany(c => c.Points).ToList();
        if (pts.Count == 0 || Viewport.ActualWidth < 50 || Viewport.ActualHeight < 50)
        {
            return;
        }
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        double w = Math.Max(1, pts.Max(p => p.X) - minX), h = Math.Max(1, pts.Max(p => p.Y) - minY);
        double scale = Math.Clamp(Math.Min((Viewport.ActualWidth - 100) / (w * Ppm), (Viewport.ActualHeight - 100) / (h * Ppm)), 0.02, 20);
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        Translate.X = (Viewport.ActualWidth - w * Ppm * scale) / 2 - minX * Ppm * scale;
        Translate.Y = (Viewport.ActualHeight - h * Ppm * scale) / 2 - minY * Ppm * scale;
        UpdateGrid();
    }

    private void Draw()
    {
        if (Drawing == null)
        {
            return;
        }
        Drawing.Children.Clear();
        _previewHits.Clear();
        _grid = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromRgb(226, 232, 239)), IsHitTestVisible = false };
        Drawing.Children.Add(_grid);
        UpdateGrid();
        try
        {
            var paths = Cam.Build(_job);
            var selection = SelectedOperations().ToHashSet();
            foreach (var path in paths.OrderBy(p => selection.Any(op => op.Id == p.OperationId) ? 1 : 0))
            {
                var op = _job.Operations.Single(o => o.Id == path.OperationId);
                bool selected = selection.Contains(op);
                Color depthColor = DepthColor(op.Depth);
                Color fill = selected ? Color.FromArgb(190, 255, 183, 0) : Color.FromArgb(105, depthColor.R, depthColor.G, depthColor.B);
                Color center = selected ? Color.FromRgb(70, 48, 0) : depthColor;
                if (path.Points.Count == 1)
                {
                    var point = path.Points[0];
                    double size = path.ToolDiameter * Ppm;
                    var disc = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(center), StrokeThickness = selected ? 2 : 1, IsHitTestVisible = false };
                    Canvas.SetLeft(disc, point.X * Ppm - size / 2);
                    Canvas.SetTop(disc, point.Y * Ppm - size / 2);
                    Drawing.Children.Add(disc);
                    _previewHits.Add(new PreviewHitRegion(op, path.Points, false, path.ToolDiameter));
                }
                else if (path.Closed && op.Tabs.Count > 0)
                {
                    var intervals = TabGeometry.ExcludedIntervals(path.Points, op, path.ToolDiameter);
                    foreach (var segment in TabGeometry.VisibleSegments(path.Points, intervals))
                    {
                        _previewHits.Add(new PreviewHitRegion(op, segment, false, path.ToolDiameter));
                        AddPolyline(segment, false, fill, Math.Max(2, path.ToolDiameter * Ppm), true);
                        AddPolyline(segment, false, center, selected ? 2 : 1.3, true, selected);
                    }
                }
                else
                {
                    _previewHits.Add(new PreviewHitRegion(op, path.Points, path.Closed, path.ToolDiameter));
                    AddPolyline(path.Points, path.Closed, fill, Math.Max(2, path.ToolDiameter * Ppm));
                    AddPolyline(path.Points, path.Closed, center, selected ? 2 : 1.3, false, selected);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Anteprima non disponibile: " + ex.Message;
        }
        foreach (var c in _job.Contours)
        {
            AddPolyline(c.Points, c.Closed, Color.FromRgb(32, 43, 55), 1.2);
        }
        if (_selected?.Kind == "Tasca")
        {
            foreach (var island in _job.Contours.Where(c => (_selected.IslandContourIds ?? new List<string>()).Contains(c.Id)))
            {
                AddPolyline(island.Points, true, Color.FromRgb(0, 137, 115), 2.5, false, true);
            }
        }
        double zx = _job.ZeroX * Ppm, zy = _job.ZeroY * Ppm;
        AddLine(zx - 12, zy, zx + 12, zy, Brushes.DodgerBlue, 2);
        AddLine(zx, zy - 12, zx, zy + 12, Brushes.DodgerBlue, 2);
        var label = new TextBlock { Text = "0,0", Foreground = Brushes.DodgerBlue, FontSize = 12, IsHitTestVisible = false };
        Canvas.SetLeft(label, zx + 7);
        Canvas.SetTop(label, zy + 4);
        Drawing.Children.Add(label);
    }

    private void UpdateGrid()
    {
        if (_grid == null || Scale.ScaleX <= 0 || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
        {
            return;
        }
        double s = Scale.ScaleX;
        double left = -Translate.X / s, top = -Translate.Y / s;
        double right = (Viewport.ActualWidth - Translate.X) / s, bottom = (Viewport.ActualHeight - Translate.Y) / s;
        const double spacing = 10 * Ppm;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (double x = Math.Floor(left / spacing) * spacing; x <= right; x += spacing)
            {
                context.BeginFigure(new Point(x, top), false, false);
                context.LineTo(new Point(x, bottom), true, false);
            }
            for (double y = Math.Floor(top / spacing) * spacing; y <= bottom; y += spacing)
            {
                context.BeginFigure(new Point(left, y), false, false);
                context.LineTo(new Point(right, y), true, false);
            }
        }
        geometry.Freeze();
        _grid.Data = geometry;
        _grid.StrokeThickness = 0.7 / s;
    }

    private void AddPolyline(List<Pt> pts, bool closed, Color color, double thickness, bool roundCaps = false, bool dashed = false)
    {
        if (pts.Count == 0)
        {
            return;
        }
        var shape = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        if (roundCaps)
        {
            shape.StrokeStartLineCap = PenLineCap.Round;
            shape.StrokeEndLineCap = PenLineCap.Round;
        }
        if (dashed)
        {
            shape.StrokeDashArray = new DoubleCollection { 3, 2 };
        }
        foreach (var p in pts)
        {
            shape.Points.Add(new Point(p.X * Ppm, p.Y * Ppm));
        }
        if (closed)
        {
            shape.Points.Add(new Point(pts[0].X * Ppm, pts[0].Y * Ppm));
        }
        Drawing.Children.Add(shape);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        Drawing.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "In DESIGN importa l'SVG, verifica le dimensioni e assegna le lavorazioni. Controlla utensile, supporto e giri mandrino. Le tasche riconoscono i contorni interni come isole: controllali e usa lo strumento Isole per modificarli. Salva il progetto o esporta il G-code.\n\nCon PASSA A CUT puoi usare il simulatore oppure collegare una scheda GRBL 1.1. Imposta manualmente lo zero pezzo e verifica ogni percorso prima della lavorazione. Il controllo CNC non è stato ancora verificato su una macchina reale.\n\nSVG: importa i contorni geometrici ignorando testi, bitmap e aspetto grafico (trasparenze, gradienti, maschere, filtri e ritagli). I riferimenti use e gli SVG annidati sono saltati; le trasformazioni CSS non sono supportate.", "RonCUT — istruzioni", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Error(Exception ex)
    {
        MessageBox.Show(this, ex.Message, "RonCUT", MessageBoxButton.OK, MessageBoxImage.Warning);
        StatusText.Text = ex.Message;
    }
}
