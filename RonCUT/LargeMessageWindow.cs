using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RonCUT;

internal sealed class LargeMessageWindow : Window
{
    private MessageBoxResult _answer = MessageBoxResult.Cancel;

    private LargeMessageWindow(Window owner, string title, string message, bool question)
    {
        Owner = owner;
        Title = "RonCUT";
        Width = 720;
        Height = question ? 320 : 340;
        MinWidth = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var layout = new Grid { Margin = new Thickness(28) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, FontSize = 27, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(25, 105, 177)), Margin = new Thickness(0, 0, 0, 14) };
        layout.Children.Add(heading);
        var text = new TextBlock { Text = message, FontSize = 17, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(37, 52, 72)) };
        var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        if (question)
        {
            AddButton(buttons, "Annulla", MessageBoxResult.Cancel, false);
            AddButton(buttons, "Non salvare", MessageBoxResult.No, false);
            AddButton(buttons, "Salva", MessageBoxResult.Yes, true);
        }
        else
        {
            AddButton(buttons, "OK", MessageBoxResult.OK, true);
        }
        Content = layout;
    }

    private void AddButton(Panel panel, string text, MessageBoxResult result, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 150,
            Height = 54,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            Background = primary ? new SolidColorBrush(Color.FromRgb(25, 113, 194)) : Brushes.WhiteSmoke,
            Foreground = primary ? Brushes.White : Brushes.Black,
            IsDefault = primary,
            IsCancel = result == MessageBoxResult.Cancel || result == MessageBoxResult.OK
        };
        button.Click += (_, _) =>
        {
            _answer = result;
            Close();
        };
        panel.Children.Add(button);
    }

    internal static void Inform(Window owner, string title, string message)
    {
        new LargeMessageWindow(owner, title, message, false).ShowDialog();
    }

    internal static MessageBoxResult AskSave(Window owner)
    {
        var dialog = new LargeMessageWindow(owner, "Modifiche non salvate", "Vuoi salvare il progetto prima di continuare?", true);
        dialog.ShowDialog();
        return dialog._answer;
    }
}
