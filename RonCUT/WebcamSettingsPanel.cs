using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RonCUT;

internal sealed class WebcamSettingsPanel : StackPanel
{
    private static readonly WebcamDevice None = new("", "Nessuna telecamera");
    private readonly WebcamStreamSettings? _saved;
    private readonly WebcamSession? _active;
    private readonly GeneralSettings _initial;
    private readonly ComboBox _devices;
    private readonly Button _refresh;
    private readonly TextBox _httpPort;
    private readonly TextBox _fps;
    private readonly TextBox _jpegQuality;
    private readonly TextBox _url;
    private readonly TextBlock _status;
    private readonly Image _image;
    private readonly Border _previewBorder;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private WebcamSession? _preview;
    private IPAddress? _localAddress;
    private string? _listError;
    private bool _updatingList;
    private bool _closed;
    public Task Released { get; private set; } = Task.CompletedTask;

    public WebcamSettingsPanel(GeneralSettings current, WebcamSession? active)
    {
        _initial = current;
        _saved = current.GetWebcamStream();
        _active = active;
        var initialDevice = string.IsNullOrEmpty(current.WebcamId) ? None :
            new WebcamDevice(current.WebcamId, string.IsNullOrWhiteSpace(current.WebcamName) ? "Webcam salvata" : current.WebcamName);
        _devices = new ComboBox
        {
            DisplayMemberPath = nameof(WebcamDevice.Name),
            ToolTip = "Telecamere rilevate da Windows"
        };
        _devices.ItemsSource = initialDevice == None ? new[] { None } : new[] { None, initialDevice };
        _devices.SelectedItem = initialDevice;
        _httpPort = NumericField(current.WebcamHttpPort, "Da 1 a 65535");
        _fps = NumericField(current.WebcamFps, "Da 1 a 30 fotogrammi al secondo");
        _jpegQuality = NumericField(current.WebcamJpegQuality, "Qualità da 1 a 100");
        _refresh = new Button
        {
            Content = "Aggiorna",
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 28
        };
        _refresh.Click += async (_, _) => await RefreshDevicesAsync();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddField(row, 0, "Webcam CNC", _devices);
        AddField(row, 1, "Porta HTTP", _httpPort);
        AddField(row, 2, "FPS", _fps);
        AddField(row, 3, "Qualità JPEG", _jpegQuality);
        Grid.SetColumn(_refresh, 4);
        row.Children.Add(_refresh);
        Children.Add(row);

        _url = Field("");
        _url.IsReadOnly = true;
        _url.Margin = new Thickness(0, 0, 0, 4);
        var urlLabel = LabelFor("URL stream MJPEG", _url);
        urlLabel.Margin = new Thickness(0, 8, 0, 2);
        Children.Add(urlLabel);
        Children.Add(_url);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Children.Add(_status);
        _image = new Image { Stretch = Stretch.Uniform };
        AutomationProperties.SetName(_image, "Anteprima webcam CNC");
        _previewBorder = new Border
        {
            Child = _image,
            Width = 224,
            Height = 126,
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Children.Add(_previewBorder);

        _devices.SelectionChanged += (_, _) =>
        {
            if (!_updatingList)
            {
                SelectPreview();
            }
        };
        _httpPort.TextChanged += (_, _) => Display();
        _fps.TextChanged += (_, _) => Display();
        _jpegQuality.TextChanged += (_, _) => Display();
        _timer.Tick += (_, _) => Display();
        Display();
    }

    public async void Start()
    {
        SelectPreview();
        _timer.Start();
        await RefreshDevicesAsync();
    }

    public void Stop()
    {
        _closed = true;
        _timer.Stop();
        StopPreview();
    }

    private async Task RefreshDevicesAsync()
    {
        _refresh.IsEnabled = false;
        try
        {
            var devices = await Task.Run(WebcamCapture.FindDevices);
            if (_closed)
            {
                return;
            }
            _listError = null;
            var selected = Selected;
            var choices = new List<WebcamDevice> { None };
            choices.AddRange(devices);
            var matching = choices.Find(device => device.Id == selected.Id);
            if (matching == null)
            {
                matching = selected;
                choices.Add(matching);
            }
            _updatingList = true;
            try
            {
                _devices.ItemsSource = choices;
                _devices.SelectedItem = matching;
            }
            finally
            {
                _updatingList = false;
            }
        }
        catch (Exception)
        {
            _listError = "Impossibile leggere l’elenco delle webcam di Windows.";
        }
        finally
        {
            _refresh.IsEnabled = true;
            if (!_closed)
            {
                Display();
            }
        }
    }

    private WebcamDevice Selected => (WebcamDevice?)_devices.SelectedItem ?? None;

    private void SelectPreview()
    {
        StopPreview();
        if (Selected != None)
        {
            if (_active?.Device.Id == Selected.Id)
            {
                _active.PreviewEnabled = true;
            }
            else
            {
                _preview = new WebcamSession(Selected, null, Released);
            }
            try
            {
                _localAddress = WebcamMjpegServer.FindLocalAddress();
            }
            catch (Exception)
            {
                _localAddress = null;
            }
        }
        Display();
    }

    private void StopPreview()
    {
        if (_active != null)
        {
            _active.PreviewEnabled = false;
        }
        if (_preview != null)
        {
            Released = _preview.Stop();
            _preview = null;
        }
    }

    private void Display()
    {
        bool selected = Selected != None;
        _httpPort.IsEnabled = _fps.IsEnabled = _jpegQuality.IsEnabled = selected;
        bool valid = TryReadOptions(out int port, out int fps, out int quality);
        bool saved = valid && _active != null && _saved?.Device.Id == Selected.Id &&
            _saved.Port == port && _saved.Fps == fps && _saved.Quality == quality;
        var session = _active?.Device.Id == Selected.Id ? _active : _preview;
        var view = session?.View;
        _image.Source = selected ? view?.Frame : null;
        _previewBorder.Visibility = _image.Source != null ? Visibility.Visible : Visibility.Collapsed;
        string? url = null;
        if (selected && valid)
        {
            url = saved && view?.Streaming == true
                ? view.Url
                : _localAddress != null ? WebcamMjpegServer.MakeUrl(_localAddress, port) : null;
        }
        url ??= "";
        if (_url.Text != url)
        {
            _url.Text = url;
        }
        _status.Text = GetStatus(selected, valid, saved, view);
    }

    private string GetStatus(bool selected, bool valid, bool saved, WebcamView? view)
    {
        if (!selected)
        {
            return _listError ?? "";
        }
        if (view?.CameraError != null)
        {
            return view.CameraError;
        }
        if (view?.Frame == null)
        {
            return "Apertura anteprima…";
        }
        if (!valid)
        {
            return WebcamStreamSettings.RangeHelp;
        }
        if (!saved)
        {
            return "Salva le impostazioni per attivare lo stream.";
        }
        if (view.StreamError != null)
        {
            return view.StreamError;
        }
        if (!view.Streaming)
        {
            return "Avvio dello stream MJPEG…";
        }
        return view.Url == null ? "Collega il PC alla rete locale per ottenere l’indirizzo HTTP." : "Stream attivo sulla rete locale.";
    }

    public void ApplyTo(GeneralSettings settings)
    {
        settings.WebcamId = Selected.Id;
        settings.WebcamName = Selected == None ? "" : Selected.Name;
        if (!TryReadOptions(out int port, out int fps, out int quality))
        {
            if (Selected != None)
            {
                throw new InvalidOperationException(WebcamStreamSettings.RangeHelp);
            }
            port = _initial.WebcamHttpPort;
            fps = _initial.WebcamFps;
            quality = _initial.WebcamJpegQuality;
        }
        settings.WebcamHttpPort = port;
        settings.WebcamFps = fps;
        settings.WebcamJpegQuality = quality;
    }

    private bool TryReadOptions(out int port, out int fps, out int quality)
    {
        static int Read(TextBox field) => int.TryParse(field.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : 0;
        port = Read(_httpPort);
        fps = Read(_fps);
        quality = Read(_jpegQuality);
        return WebcamStreamSettings.IsValid(port, fps, quality);
    }

    private static TextBox NumericField(int value, string hint)
    {
        var field = Field(value.ToString(CultureInfo.InvariantCulture));
        field.ToolTip = hint;
        return field;
    }

    private static TextBox Field(string value) => new()
    {
        Text = value,
        Height = 28,
        Padding = new Thickness(6, 3, 6, 3)
    };

    private static Label LabelFor(string content, Control target)
    {
        var label = new Label
        {
            Content = content,
            Target = target,
            Padding = new Thickness(0),
            FontWeight = FontWeights.SemiBold
        };
        AutomationProperties.SetLabeledBy(target, label);
        return label;
    }

    private static void AddField(Grid row, int column, string caption, Control field)
    {
        var panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) };
        var label = LabelFor(caption, field);
        label.Margin = new Thickness(0, 0, 0, 3);
        panel.Children.Add(label);
        field.Height = 28;
        panel.Children.Add(field);
        Grid.SetColumn(panel, column);
        row.Children.Add(panel);
    }
}
