using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace RonCUT;

public sealed class GeneralSettingsWindow : Window
{
    private readonly TextBox _safeZ;
    private readonly TextBox _tabLength;
    private readonly TextBox _communicationsApiUrl;
    public GeneralSettings Value { get; private set; }

    public GeneralSettingsWindow(GeneralSettings current)
    {
        Value = new GeneralSettings
        {
            SafeZ = current.SafeZ,
            TabLength = current.TabLength,
            CommunicationsApiUrl = current.CommunicationsApiUrl
        };
        Title = "RonCUT — impostazioni generali";
        Width = 720;
        Height = 490;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel
        {
            Margin = new Thickness(20)
        };
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var cancel = new Button
        {
            Content = "Annulla",
            IsCancel = true,
            Padding = new Thickness(18, 6, 18, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var save = new Button
        {
            Content = "Salva impostazioni",
            IsDefault = true,
            Padding = new Thickness(18, 6, 18, 6)
        };
        save.Click += Save_Click;
        bottom.Children.Add(cancel);
        bottom.Children.Add(save);
        root.Children.Add(bottom);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Questi valori valgono per tutti i progetti di RonCUT su questo PC.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 15)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Z di sicurezza (mm)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _safeZ = new TextBox
        {
            Text = current.SafeZ.ToString("G", CultureInfo.CurrentCulture),
            Height = 30,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(_safeZ);
        panel.Children.Add(new TextBlock
        {
            Text = "Lunghezza ponticelli (mm)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        _tabLength = new TextBox
        {
            Text = current.TabLength.ToString("G", CultureInfo.CurrentCulture),
            Height = 30,
            Padding = new Thickness(7, 3, 7, 3)
        };
        panel.Children.Add(_tabLength);
        panel.Children.Add(new Separator
        {
            Margin = new Thickness(0, 18, 0, 12)
        });
        _communicationsApiUrl = AddUrlField(panel, "URL API REST per comunicazioni", current.CommunicationsApiUrl);
        panel.Children.Add(new TextBlock
        {
            Text = "Incolla l’indirizzo completo con i parametri della tua API. RonCUT aggiunge msg in automatico. Lascia vuoto per disattivare le notifiche.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        });
        Content = root;
    }

    private static TextBox AddUrlField(Panel panel, string label, string? value)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var box = new TextBox
        {
            Text = value ?? "",
            MinLines = 3,
            MaxLines = 3,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        panel.Children.Add(box);
        return box;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Value = ReadSettings();
            DialogResult = true;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Impostazioni generali", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private GeneralSettings ReadSettings()
    {
        var settings = new GeneralSettings
        {
            SafeZ = Parse(_safeZ.Text, "Z di sicurezza"),
            TabLength = Parse(_tabLength.Text, "Lunghezza ponticelli"),
            CommunicationsApiUrl = _communicationsApiUrl.Text.Trim()
        };
        settings.Validate();
        return settings;
    }

    private static double Parse(string text, string name)
    {
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            throw new InvalidOperationException(name + ": inserisci un numero valido.");
        }
        return value;
    }
}
