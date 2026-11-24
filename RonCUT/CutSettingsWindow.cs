using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RonCUT;

public sealed class CutSettingsWindow : Window
{
    private readonly CutSettings _settings;
    private readonly string _path;
    private readonly ComboBox _ports = new ComboBox { IsEditable = true, Height = 34 };
    private readonly TextBox _baud = new TextBox { Height = 32 };
    private readonly TextBox _slowStep = new TextBox { Height = 32 };
    private readonly TextBox _slowFeed = new TextBox { Height = 32 };
    private readonly TextBox _fastStep = new TextBox { Height = 32 };
    private readonly TextBox _fastFeed = new TextBox { Height = 32 };
    private readonly TextBlock _feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private bool _loading;

    public CutSettingsWindow(CutSettings settings, string path)
    {
        _settings = settings;
        _path = path;
        Title = "RonCUT — impostazioni CNC";
        Width = 590;
        Height = 515;
        MinWidth = 500;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(22) };
        var close = new Button { Content = "Chiudi", FontSize = 17, Height = 48, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel();
        scroll.Content = panel;
        root.Children.Add(scroll);
        Content = root;
        panel.Children.Add(new TextBlock { Text = "Collegamento CNC", FontSize = 23, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Porta COM", Margin = new Thickness(0, 14, 0, 4) });
        var portsRow = new DockPanel();
        var refresh = new Button { Content = "Aggiorna", Width = 100, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(refresh, Dock.Right);
        portsRow.Children.Add(refresh);
        portsRow.Children.Add(_ports);
        panel.Children.Add(portsRow);
        panel.Children.Add(new TextBlock { Text = "Velocità seriale (baud)", Margin = new Thickness(0, 10, 0, 4) });
        panel.Children.Add(_baud);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 20, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Movimento manuale", FontSize = 21, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Le modifiche valide sono salvate automaticamente.", Foreground = Brushes.SlateGray, Margin = new Thickness(0, 4, 0, 12) });
        var profiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        profiles.Children.Add(ProfileFields(nameof(CutSettings.Slow), _settings.Slow, _slowStep, _slowFeed));
        profiles.Children.Add(ProfileFields(nameof(CutSettings.Fast), _settings.Fast, _fastStep, _fastFeed));
        panel.Children.Add(profiles);
        panel.Children.Add(_feedback);
        _loading = true;
        RefreshPorts();
        _ports.Text = _settings.Port;
        _baud.Text = _settings.Baud.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        foreach (var box in new[] { _slowStep, _slowFeed, _fastStep, _fastFeed })
        {
            box.TextChanged += (_, _) => StoreProfiles();
        }
        _ports.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => StorePort()));
        _ports.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(StorePort));
        _baud.TextChanged += (_, _) => StorePort();
        refresh.Click += (_, _) => RefreshPorts();
    }

    private static StackPanel ProfileFields(string name, JogProfile profile, TextBox step, TextBox feed)
    {
        var fields = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        fields.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold });
        var stepLabel = new Label { Content = "Passo (mm)", Target = step, Padding = new Thickness(0, 6, 0, 4) };
        var feedLabel = new Label { Content = "Feed (mm/min)", Target = feed, Padding = new Thickness(0, 6, 0, 4) };
        fields.Children.Add(stepLabel);
        fields.Children.Add(step);
        fields.Children.Add(feedLabel);
        fields.Children.Add(feed);
        step.Text = profile.Step.ToString("G", CultureInfo.CurrentCulture);
        feed.Text = profile.Feed.ToString("G", CultureInfo.CurrentCulture);
        return fields;
    }

    private void RefreshPorts()
    {
        string value = _ports.Text;
        _ports.ItemsSource = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
        _ports.Text = value;
    }

    private void StorePort()
    {
        if (_loading)
        {
            return;
        }
        if (!int.TryParse(_baud.Text, out int baud) || baud < 1200 || baud > 1000000)
        {
            _feedback.Text = "Inserisci una velocità seriale valida.";
            _feedback.Foreground = Brushes.Firebrick;
            return;
        }
        _settings.Port = _ports.Text.Trim();
        _settings.Baud = baud;
        Persist();
    }

    private void StoreProfiles()
    {
        if (!Parse(_slowStep.Text, 1000, out double slowStep) || !Parse(_slowFeed.Text, 100000, out double slowFeed) ||
            !Parse(_fastStep.Text, 1000, out double fastStep) || !Parse(_fastFeed.Text, 100000, out double fastFeed))
        {
            _feedback.Text = "Inserisci valori validi per passo e feed di Slow e Fast.";
            _feedback.Foreground = Brushes.Firebrick;
            return;
        }
        _settings.Slow.Step = slowStep;
        _settings.Slow.Feed = slowFeed;
        _settings.Fast.Step = fastStep;
        _settings.Fast.Feed = fastFeed;
        Persist();
    }

    private static bool Parse(string text, double max, out double value)
    {
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && value > 0 && value <= max;
    }

    private void Persist()
    {
        try
        {
            _settings.Save(_path);
            _feedback.Text = "Impostazioni salvate automaticamente.";
            _feedback.Foreground = Brushes.SeaGreen;
        }
        catch (Exception ex)
        {
            _feedback.Text = "Impossibile salvare: " + ex.Message;
            _feedback.Foreground = Brushes.Firebrick;
        }
    }

}
