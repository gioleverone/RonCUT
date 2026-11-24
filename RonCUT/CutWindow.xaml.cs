using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RonCUT;

public partial class CutWindow : Window
{
    private readonly IReadOnlyList<CutPhase> _phases;
    private readonly string _settingsPath;
    private readonly CutSettings _settings;
    private readonly CutPreview _preview;
    private readonly GeneralSettings _generalSettings;
    private readonly CutNotificationService _notifications;
    private bool _closed;
    private readonly DispatcherTimer _timer = new DispatcherTimer();
    private readonly DispatcherTimer _overrideTimer = new DispatcherTimer();
    private GrblController? _controller;
    private bool _simulation;
    private bool _connecting;
    private bool _feedChanged;
    private bool _spindleChanged;
    private int _timerTicks;
    private double _nominalConsumed;
    private double _lastElapsed;
    private JogPress? _jogPress;
    private bool _jogStepPending;
    private bool _closingAfterJog;
    private readonly Func<Key, bool> _jogKeyIsDown;
    private readonly Func<string, int, IGrblTransport> _cncTransportFactory;

    private sealed class JogPress
    {
        public required GrblController Controller { get; init; }
        public required string Direction { get; init; }
        public double Step { get; init; }
        public double Feed { get; init; }
        public Key? Key { get; init; }
        public Button? Button { get; init; }
        public bool Started { get; set; }
        public CancellationTokenSource DelayCancellation { get; } = new CancellationTokenSource();
    }

    public CutWindow(string gcode, double toolDiameter, string? settingsPath = null, GeneralSettings? generalSettings = null, CutNotificationService? notifications = null, Func<Key, bool>? jogKeyIsDown = null)
        : this(new[] { new CutPhase(gcode, "", toolDiameter) }, settingsPath, generalSettings, notifications, jogKeyIsDown)
    {
    }

    public CutWindow(IReadOnlyList<CutPhase> phases, string? settingsPath = null, GeneralSettings? generalSettings = null, CutNotificationService? notifications = null, Func<Key, bool>? jogKeyIsDown = null, Func<string, int, IGrblTransport>? cncTransportFactory = null)
    {
        if (phases.Count == 0)
        {
            throw new ArgumentException("Manca la fase iniziale.", nameof(phases));
        }
        _phases = phases.ToArray();
        _settingsPath = settingsPath ?? CutSettings.DefaultPath;
        _settings = CutSettings.Load(_settingsPath);
        _generalSettings = generalSettings ?? new GeneralSettings();
        _notifications = notifications ?? new CutNotificationService();
        _jogKeyIsDown = jogKeyIsDown ?? Keyboard.IsKeyDown;
        _cncTransportFactory = cncTransportFactory ?? ((port, baud) => new SerialGrblTransport(port, baud));
        InitializeComponent();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Window_PreviewKeyDown), handledEventsToo: true);
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Window_PreviewKeyUp), handledEventsToo: true);
        _notifications.StatusChanged += Notification_StatusChanged;
        _preview = new CutPreview(_phases);
        PreviewHost.Children.Add(_preview);
        RefreshJogProfiles();
        RefreshPortLabel();
        _timer.Interval = TimeSpan.FromMilliseconds(80);
        _timer.Tick += Timer_Tick;
        _timer.Start();
        _overrideTimer.Interval = TimeSpan.FromMilliseconds(250);
        _overrideTimer.Tick += OverrideTimer_Tick;
        UpdateUi();
    }

    private void RefreshJogProfiles()
    {
        SlowJogButton.IsChecked = _settings.SelectedProfile == nameof(CutSettings.Slow);
        FastJogButton.IsChecked = _settings.SelectedProfile == nameof(CutSettings.Fast);
        SlowJogButton.ToolTip = DescribeJogProfile(_settings.Slow);
        FastJogButton.ToolTip = DescribeJogProfile(_settings.Fast);
    }

    private static string DescribeJogProfile(JogProfile profile) =>
        $"Passo {profile.Step:0.###} mm · feed {profile.Feed:0.###} mm/min";

    private void RefreshPortLabel()
    {
        PortLabel.Text = string.IsNullOrWhiteSpace(_settings.Port) ? "Porta non configurata" : _settings.Port + " · " + _settings.Baud + " baud";
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        string previousPort = _settings.Port;
        int previousBaud = _settings.Baud;
        var window = new CutSettingsWindow(_settings, _settingsPath) { Owner = this };
        window.ShowDialog();
        RefreshJogProfiles();
        if (!_simulation && _controller?.IsConnected == true)
        {
            if (previousPort != _settings.Port || previousBaud != _settings.Baud)
            {
                StatusText.Text = "La nuova configurazione seriale sarà usata alla prossima apertura di CUT.";
            }
        }
        else
        {
            RefreshPortLabel();
            if (!_simulation)
            {
                await ConnectConfiguredCncAsync();
            }
        }
        UpdateUi();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await ConnectConfiguredCncAsync();
    }

    private async Task ConnectConfiguredCncAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.Port))
        {
            StatusText.Text = "Configura la porta COM nelle impostazioni CNC oppure avvia il simulatore.";
            return;
        }
        try
        {
            await ConnectAsync(_cncTransportFactory(_settings.Port.Trim(), _settings.Baud), false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Connessione non riuscita: " + ex.Message;
        }
    }

    private async void Simulate_Click(object sender, RoutedEventArgs e)
    {
        if (!SimulateButton.IsEnabled)
        {
            return;
        }
        Disconnect();
        await ConnectAsync(new SimulatedGrblTransport(), true);
    }

    private async Task ConnectAsync(IGrblTransport transport, bool simulation)
    {
        if (_controller != null && !_controller.IsConnected && !_controller.IsRunning && !_connecting)
        {
            Disconnect();
        }
        if (_controller != null || _connecting)
        {
            transport.Dispose();
            return;
        }
        _connecting = true;
        _simulation = simulation;
        var controller = new GrblController(transport);
        _controller = controller;
        controller.Changed += Controller_Changed;
        controller.Log += Controller_Log;
        UpdateUi();
        try
        {
            await controller.ConnectAsync();
            StatusText.Text = simulation ? "SIMULAZIONE: nessun comando viene inviato alla CNC." : "Collegamento GRBL 1.1 riconosciuto. Attendi lo stato della macchina.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Connessione non riuscita: " + ex.Message;
            controller.Changed -= Controller_Changed;
            controller.Log -= Controller_Log;
            controller.Dispose();
            _controller = null;
        }
        finally
        {
            _connecting = false;
            UpdateUi();
        }
    }

    private void Controller_Changed()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(new Action(UpdateUi));
        }
    }

    private void Notification_StatusChanged(string message)
    {
        if (!_closed && !Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closed)
                {
                    NotificationText.Text = message;
                }
            }));
        }
    }

    private void Controller_Log(string message)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = message));
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var press = _jogPress;
            if (press != null)
            {
                if ((press.Key.HasValue && !_jogKeyIsDown(press.Key.Value)) || (press.Button != null && Mouse.LeftButton != MouseButtonState.Pressed))
                {
                    _ = EndJogPressAsync(true);
                }
                else if (press.Started)
                {
                    press.Controller.RenewJogHold();
                }
            }
            if (++_timerTicks % 3 == 0)
            {
                _controller?.QueryStatus();
            }
            _preview.ShowPosition(_controller?.WorkPosition, _controller?.SpindleRunning == true);
            UpdateUi();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Comunicazione: " + ex.Message + ". Verifica lo stato della macchina.";
        }
    }

    private void UpdateUi()
    {
        if (!IsInitialized)
        {
            return;
        }
        var c = _controller;
        UpdateToolChangeState(c);
        bool connected = c?.IsConnected == true;
        bool running = c?.IsRunning == true;
        bool ready = c?.IsReady == true && !_connecting;
        bool manualBusy = _jogPress != null || c?.IsJogging == true || _jogStepPending;
        if (c?.IsResetPending == true && !c.IsResetAwaitingConfirmation)
        {
            StatusText.Text = "STOP non confermato da GRBL: usa l'arresto fisico e verifica la macchina.";
        }
        ConnectionLabel.Text = _connecting ? "CONNESSIONE…" : connected
            ? (_simulation ? "SIMULAZIONE · " : "CNC · ") + c!.State.ToUpperInvariant() : "NON CONNESSA";
        ConnectionLabel.Foreground = _simulation && connected ? Brushes.DarkOrange : connected ? Brushes.SeaGreen : Brushes.Firebrick;
        SimulateButton.IsEnabled = !_programActive && !running && !manualBusy && !_connecting
            && (!connected || (!_simulation && ready && !c!.SpindleRunning));
        SetupButton.IsEnabled = !_programActive && !running && !manualBusy && !_connecting;
        ManualControls.IsEnabled = connected && !running && !_connecting && (ready || manualBusy) && ManualMotionAllowed(c);
        JogProfileButtons.IsEnabled = ready && !manualBusy;
        JogStopButton.IsEnabled = connected && !running && !_connecting && (ready || manualBusy);
        ZeroControls.IsEnabled = ready && !manualBusy && !_programActive;
        StartButton.IsEnabled = ready && !manualBusy && !_programActive && !_restartRequiresReopen && !string.IsNullOrWhiteSpace(_phases[0].Gcode);
        ResumeAfterToolChangeButton.IsEnabled = CanResumeAfterToolChange(c) && !manualBusy;
        ToolChangeZeroZButton.IsEnabled = _toolChange != null && ready && !manualBusy && ManualMotionAllowed(c);
        PauseButton.IsEnabled = running && (c!.State == "Run" || c.IsPaused);
        PauseButton.Content = c?.IsPaused == true ? "▶   RIPRENDI" : "Ⅱ   PAUSA";
        StopButton.IsEnabled = connected && !_connecting;
        UnlockButton.IsEnabled = connected && !running && c!.State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase);
        FeedSlider.IsEnabled = running;
        SpindleSlider.IsEnabled = running;
        PositionLabel.Text = c?.WorkPosition is MachinePosition pos
            ? string.Format(CultureInfo.CurrentCulture, "X {0:0.0}\nY {1:0.0}\nZ {2:0.0}", pos.X, pos.Y, pos.Z)
            : "X  —\nY  —\nZ  —";
        MotionLabel.Text = !connected ? "CNC non connessa" : c!.Version + (c.SpindleRunning ? " · mandrino attivo" : " · mandrino non segnalato attivo");
        FeedLabel.Text = "Avanzamento: richiesta " + (int)FeedSlider.Value + "% · GRBL " + (c?.FeedOverride ?? 100) + "%";
        SpindleLabel.Text = "Mandrino: richiesta " + (int)SpindleSlider.Value + "% · GRBL " + (c?.SpindleOverride ?? 100) + "%";
        if (_preview.NominalSeconds <= 0)
        {
            EstimateLabel.Text = "Tempo stimato: —";
        }
        else if (!running)
        {
            EstimateLabel.Text = _toolChange != null ? "In attesa del cambio utensile" : "Tempo stimato: " + FormatTime(_preview.NominalSeconds);
        }
        else
        {
            double rate = Math.Max(0.1, c!.FeedOverride / 100.0);
            double elapsed = c.Elapsed.TotalSeconds;
            _nominalConsumed += Math.Max(0, elapsed - _lastElapsed) * rate;
            _lastElapsed = elapsed;
            double left = Math.Max(0, (_preview.NominalSeconds - _nominalConsumed) / rate);
            EstimateLabel.Text = left < 1 ? "Tempo restante: meno di 1 s*" : "Tempo restante: " + FormatTime(left) + "*";
        }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds - 1));
        return time.TotalHours >= 1 ? ((int)time.TotalHours) + " h " + time.Minutes + " min" : time.Minutes + " min " + time.Seconds + " s";
    }

    private void Disconnect()
    {
        if (_controller == null)
        {
            return;
        }
        try
        {
            _controller.Changed -= Controller_Changed;
            _controller.Log -= Controller_Log;
            _controller.Dispose();
            _controller = null;
            _preview.ShowPosition(null, false);
            StatusText.Text = "Disconnessa.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        UpdateUi();
    }

    private void JogProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsEnabled: true, Tag: string profile } || profile == _settings.SelectedProfile)
        {
            return;
        }
        string previous = _settings.SelectedProfile;
        try
        {
            _settings.SelectedProfile = profile;
            _settings.Save(_settingsPath);
            StatusText.Text = profile + " · " + DescribeJogProfile(_settings.ActiveProfile);
        }
        catch (Exception ex)
        {
            _settings.SelectedProfile = previous;
            StatusText.Text = "Impossibile salvare il profilo: " + ex.Message;
        }
        RefreshJogProfiles();
    }

    private async void Jog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string direction)
        {
            await Jog(direction);
        }
    }

    private async Task Jog(string direction)
    {
        if (_controller == null || direction.Length != 2)
        {
            return;
        }
        var profile = _settings.ActiveProfile;
        await JogStepAsync(_controller, direction, profile.Step, profile.Feed);
    }

    private async Task JogStepAsync(GrblController controller, string direction, double step, double feed)
    {
        if (_jogStepPending || _jogPress != null || !controller.IsReady || !ManualMotionAllowed(controller))
        {
            return;
        }
        _jogStepPending = true;
        try
        {
            await controller.JogAsync(direction[0], direction[1] == '+' ? 1 : -1, step, feed);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _jogStepPending = false;
            UpdateUi();
        }
    }

    private void BeginJogPress(string direction, Key? key, Button? button)
    {
        if (_jogPress != null || _jogStepPending || _controller?.IsReady != true || !ManualMotionAllowed(_controller))
        {
            return;
        }
        if (key.HasValue && IsActive)
        {
            // Il profilo, START e gli zeri si disabilitano durante il jog. La vista resta disponibile per ricevere KeyUp.
            PreviewHost.Focus();
        }
        var profile = _settings.ActiveProfile;
        var press = new JogPress
        {
            Controller = _controller,
            Direction = direction,
            Step = profile.Step,
            Feed = profile.Feed,
            Key = key,
            Button = button
        };
        _jogPress = press;
        if (button != null && !button.CaptureMouse())
        {
            _jogPress = null;
            press.DelayCancellation.Dispose();
            return;
        }
        _ = RunHeldJogAsync(press);
        UpdateUi();
    }

    private async Task RunHeldJogAsync(JogPress press)
    {
        try
        {
            await Task.Delay(250, press.DelayCancellation.Token);
            if (_jogPress != press || press.DelayCancellation.IsCancellationRequested || !ManualMotionAllowed(press.Controller))
            {
                return;
            }
            press.Started = true;
            StatusText.Text = "Movimento continuo " + press.Direction + ": rilascia per fermare.";
            await press.Controller.JogContinuousAsync(press.Direction[0], press.Direction[1] == '+' ? 1 : -1, press.Feed);
            if (_jogPress == press)
            {
                StatusText.Text = "Movimento manuale arrestato. Rilascia e premi nuovamente per proseguire.";
            }
        }
        catch (OperationCanceledException) when (press.DelayCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            if (_jogPress == press)
            {
                _jogPress = null;
                press.Button?.ReleaseMouseCapture();
            }
            press.DelayCancellation.Dispose();
            UpdateUi();
        }
    }

    private JogPress? ClearJogInput()
    {
        var press = _jogPress;
        _jogPress = null;
        if (press != null)
        {
            press.DelayCancellation.Cancel();
            press.Button?.ReleaseMouseCapture();
        }
        return press;
    }

    private async Task<bool> EndJogPressAsync(bool forceStop)
    {
        var press = ClearJogInput();
        try
        {
            var controller = press?.Controller ?? _controller;
            if (controller != null && (press?.Started == true || (forceStop && controller.IsJogging)))
            {
                await controller.CancelJogAsync();
                StatusText.Text = "Movimento manuale arrestato; coda annullata.";
            }
            else if (!forceStop && press != null)
            {
                await JogStepAsync(press.Controller, press.Direction, press.Step, press.Feed);
            }
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Arresto manuale non confermato: " + ex.Message;
            return false;
        }
        finally
        {
            UpdateUi();
        }
    }

    private void Jog_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && button.Tag is string direction)
        {
            e.Handled = true;
            BeginJogPress(direction, null, button);
        }
    }

    private async void Jog_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_jogPress?.Button == sender)
        {
            await EndJogPressAsync(false);
        }
    }

    private async void Jog_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_jogPress?.Button == sender)
        {
            await EndJogPressAsync(true);
        }
    }

    private async void JogStop_Click(object sender, RoutedEventArgs e)
    {
        await EndJogPressAsync(true);
    }

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        await EndJogPressAsync(true);
    }

    private async void Window_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_jogPress?.Key != null)
        {
            await EndJogPressAsync(true);
        }
    }

    private async void ZeroXY_Click(object sender, RoutedEventArgs e) => await SetZero(true);
    private async void ZeroZ_Click(object sender, RoutedEventArgs e) => await SetZero(false);

    private async Task SetZero(bool xy)
    {
        try
        {
            if (_controller != null && !_programActive)
            {
                await _controller.SetZeroAsync(xy);
                StatusText.Text = xy ? "Zero XY impostato nella posizione attuale." : "Zero Z impostato nella posizione attuale.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var controller = _controller;
        if (controller == null || _programActive || _restartRequiresReopen || !StartButton.IsEnabled)
        {
            return;
        }
        _programActive = true;
        try
        {
            FeedSlider.Value = 100;
            SpindleSlider.Value = 100;
            _nominalConsumed = 0;
            _lastElapsed = 0;
            _preview.SetToolDiameter(_phases[0].ToolDiameter);
            NotificationText.Text = "";
            StatusText.Text = _simulation ? "SIMULAZIONE in corso." : "Lavorazione avviata.";
            bool completed = await _notifications.RunAsync(() => StartProgram(controller), _simulation, _generalSettings);
            if (!string.IsNullOrEmpty(controller.LastError))
            {
                StatusText.Text = "Lavorazione interrotta: " + controller.LastError;
            }
            else if (completed)
            {
                StatusText.Text = _simulation ? "Simulazione completata." : "GRBL segnala lavorazione completata.";
                if (_restartRequiresReopen)
                {
                    StatusText.Text += " Per ripetere, rimonta il primo utensile e riapri CUT.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _programActive = false;
            _toolChange = null;
            UpdateUi();
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_controller?.IsPaused == true)
            {
                _controller.Resume();
                StatusText.Text = "Ripresa richiesta.";
            }
            else
            {
                _controller?.Pause();
                StatusText.Text = "Pausa richiesta: attendo GRBL. Il mandrino può restare acceso.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClearJogInput();
            AbortToolChange("Sequenza annullata. Controlla i riferimenti prima di un nuovo avvio.");
            _controller?.Stop();
            StatusText.Text = "STOP inviato. Verifica stato e posizione della macchina; il pulsante fisico resta necessario per le emergenze.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "STOP non confermato: " + ex.Message + ". Usa l'arresto fisico e verifica la macchina.";
        }
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_controller != null)
            {
                await _controller.UnlockAsync();
                StatusText.Text = "Sblocco richiesto. Verifica posizione e stato della macchina.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Feed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized && _controller?.IsRunning == true)
        {
            _feedChanged = true;
            _overrideTimer.Stop();
            _overrideTimer.Start();
        }
    }

    private void Spindle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized && _controller?.IsRunning == true)
        {
            _spindleChanged = true;
            _overrideTimer.Stop();
            _overrideTimer.Start();
        }
    }

    private void OverrideTimer_Tick(object? sender, EventArgs e)
    {
        _overrideTimer.Stop();
        try
        {
            _controller?.SetOverrides(_feedChanged ? (int)FeedSlider.Value : null, _spindleChanged ? (int)SpindleSlider.Value : null);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        _feedChanged = false;
        _spindleChanged = false;
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string view)
        {
            _preview.SetView(view);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.NumPad5 || e.Key == Key.Escape) && _controller?.IsRunning != true)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                _ = EndJogPressAsync(true);
            }
            return;
        }
        string? direction = e.Key switch
        {
            Key.NumPad4 => "X-",
            Key.NumPad6 => "X+",
            Key.NumPad8 => "Y+",
            Key.NumPad2 => "Y-",
            Key.NumPad9 => "Z+",
            Key.NumPad3 => "Z-",
            _ => null
        };
        if (direction != null)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                BeginJogPress(direction, e.Key, null);
            }
        }
    }

    private async void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_jogPress?.Key == e.Key)
        {
            e.Handled = true;
            await EndJogPressAsync(false);
        }
    }

    private void Design_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_programActive || _controller?.IsRunning == true || _connecting || _controller?.IsResetAwaitingConfirmation == true)
        {
            e.Cancel = true;
            StatusText.Text = _toolChange != null ? "Per tornare a DESIGN, annulla la sequenza con lo STOP della lavorazione."
                : "Attendi che la lavorazione e il reset siano confermati prima di tornare a DESIGN.";
            return;
        }
        if (_jogPress != null || _controller?.IsJogging == true)
        {
            e.Cancel = true;
            if (!_closingAfterJog)
            {
                _closingAfterJog = true;
                bool stopped = await EndJogPressAsync(true);
                _closingAfterJog = false;
                if (stopped && _controller?.IsJogging != true)
                {
                    Close();
                }
            }
            return;
        }
        _timer.Stop();
        _overrideTimer.Stop();
        _closed = true;
        _notifications.StatusChanged -= Notification_StatusChanged;
        Disconnect();
    }
}
