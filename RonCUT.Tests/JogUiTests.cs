using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RonCUT;

internal static class JogUiTests
{
    public static void Run(CutWindow window, GrblController controller, string directory, Action<bool, string> check, Action<bool> setKeyHeld)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(CutWindow).GetMethod(name, flags)!.Invoke(window, args);
        var fastButton = (RadioButton)window.FindName("FastJogButton");
        var previewHost = (Grid)window.FindName("PreviewHost");
        var settings = (CutSettings)typeof(CutWindow).GetField("_settings", flags)!.GetValue(window)!;
        var profile = settings.ActiveProfile;
        double oldStep = profile.Step, oldFeed = profile.Feed;
        profile.Step = 0.25;
        profile.Feed = 600;
        try
        {
            double beforeY = controller.WorkPosition!.Y;
            Call("BeginJogPress", "Y-", null, null);
            var released = (Task)Call("EndJogPressAsync", false)!;
            Program.PumpUntil(() => released.IsCompleted && controller.IsReady, 4000);
            released.GetAwaiter().GetResult();
            check(Math.Abs(controller.WorkPosition!.Y - beforeY + 0.25) < 0.002, "interfaccia jog: il clic breve esegue il passo del profilo");

            foreach (var source in new UIElement[] { previewHost, (Button)window.FindName("StartButton") })
            {
                double beforeZ = controller.WorkPosition!.Z;
                source.Focus();
                RaiseKey(source, Key.NumPad9, Keyboard.PreviewKeyDownEvent, handled: true);
                RaiseKey(previewHost, Key.NumPad9, Keyboard.PreviewKeyUpEvent, handled: true);
                Program.PumpUntil(() => controller.IsReady && controller.WorkPosition!.Z > beforeZ, 4000);
                check(Math.Abs(controller.WorkPosition!.Z - beforeZ - 0.25) < 0.002,
                    "interfaccia jog: il tastierino intercettato dalla finestra esegue un solo passo da " + ((FrameworkElement)source).Name);
            }

            double beforeX = controller.WorkPosition!.X;
            fastButton.Focus();
            var keyDown = RaiseKey(fastButton, Key.NumPad6, Keyboard.PreviewKeyDownEvent);
            check(keyDown.Handled && (!window.IsActive || previewHost.IsKeyboardFocused),
                "interfaccia jog: il tastierino funziona dal selettore del profilo e la vista riceve il rilascio");
            Program.PumpUntil(() => controller.IsJogging && controller.WorkPosition!.X > beforeX + 0.1, 4000);
            check(((StackPanel)window.FindName("ManualControls")).IsEnabled && ((Button)window.FindName("JogStopButton")).IsEnabled &&
                !((Button)window.FindName("StartButton")).IsEnabled && !((StackPanel)window.FindName("ZeroControls")).IsEnabled,
                "interfaccia jog: direzioni e STOP disponibili durante la pressione, START e zeri bloccati");
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "RonCUT-jog-continuo.png")))
            {
                encoder.Save(file);
            }
            var keyUp = RaiseKey(previewHost, Key.NumPad6, Keyboard.PreviewKeyUpEvent);
            Program.PumpUntil(() => controller.IsReady && !controller.IsJogging, 4000);
            double stoppedX = controller.WorkPosition!.X;
            Wait(260);
            check(keyUp.Handled && Math.Abs(controller.WorkPosition!.X - stoppedX) < 0.001, "interfaccia jog: KeyUp ferma il movimento continuo senza movimenti residui");

            Call("BeginJogPress", "X+", null, null);
            Program.PumpUntil(() => controller.IsJogging, 4000);
            ((Button)window.FindName("JogStopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.PumpUntil(() => controller.IsReady && !controller.IsJogging, 4000);
            check(typeof(CutWindow).GetField("_jogPress", flags)!.GetValue(window) == null, "interfaccia jog: STOP centrale annulla anche la pressione attiva");

            Call("BeginJogPress", "X+", Key.NumPad6, null);
            Program.PumpUntil(() => controller.IsJogging, 4000);
            var keypadStop = RaiseKey(previewHost, Key.NumPad5, Keyboard.PreviewKeyDownEvent);
            Program.PumpUntil(() => controller.IsReady && !controller.IsJogging, 4000);
            var ignoredRelease = RaiseKey(previewHost, Key.NumPad6, Keyboard.PreviewKeyUpEvent);
            check(keypadStop.Handled && !ignoredRelease.Handled && controller.IsReady,
                "interfaccia jog: il 5 del tastierino esegue STOP e annulla il movimento accodato al rilascio");

            Call("BeginJogPress", "X+", Key.NumPad6, null);
            Program.PumpUntil(() => controller.IsJogging, 4000);
            setKeyHeld(false);
            Program.PumpUntil(() => controller.IsReady && !controller.IsJogging, 4000);
            check(controller.IsReady, "interfaccia jog: il controllo dello stato del tasto rileva anche un KeyUp perso");
            setKeyHeld(true);

            beforeX = controller.WorkPosition!.X;
            Call("BeginJogPress", "X+", null, null);
            Call("Window_Deactivated", window, EventArgs.Empty);
            Wait(400);
            check(controller.IsReady && !controller.IsJogging && Math.Abs(controller.WorkPosition!.X - beforeX) < 0.001,
                "interfaccia jog: perdita del focus prima della soglia non avvia movimenti ritardati");
            Call("BeginJogPress", "Z+", null, null);
            Program.PumpUntil(() => controller.IsJogging, 4000);
            Call("Window_Deactivated", window, EventArgs.Empty);
            Program.PumpUntil(() => controller.IsReady && !controller.IsJogging, 4000);
            check(controller.IsReady, "interfaccia jog: perdita del focus durante la pressione arresta il jog");
        }
        finally
        {
            profile.Step = oldStep;
            profile.Feed = oldFeed;
        }
    }

    private static KeyEventArgs RaiseKey(UIElement source, Key key, RoutedEvent routedEvent, bool handled = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, key)
        {
            RoutedEvent = routedEvent,
            Handled = handled
        };
        source.RaiseEvent(args);
        return args;
    }

    private static void Wait(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        Program.PumpUntil(() => DateTime.UtcNow >= end, milliseconds + 1000);
    }
}
