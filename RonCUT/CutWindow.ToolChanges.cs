using System.Windows;

namespace RonCUT;

public partial class CutWindow
{
    private bool _programActive;
    private bool _restartRequiresReopen;
    private ToolChangePause? _toolChange;

    private sealed class ToolChangePause(GrblController controller, long epoch)
    {
        public GrblController Controller { get; } = controller;
        public long Epoch { get; } = epoch;
        public TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ZeroZConfirmed { get; set; }
        public bool SettingZeroZ { get; set; }
    }

    private bool ReferenceIsValid(ToolChangePause pause, GrblController? controller)
        => controller != null && controller == pause.Controller && controller.IsConnected &&
           !controller.IsResetPending && controller.CoordinateEpoch == pause.Epoch;

    private bool ManualMotionAllowed(GrblController? controller)
        => !_programActive || (_toolChange is ToolChangePause pause && !pause.Resume.Task.IsCompleted &&
            ReferenceIsValid(pause, controller) && !pause.SettingZeroZ && !controller!.SpindleRunning);

    private bool CanResumeAfterToolChange(GrblController? controller)
        => _toolChange?.ZeroZConfirmed == true && ManualMotionAllowed(controller) && controller?.IsReady == true &&
           _jogPress == null && !_jogStepPending;

    private Task<bool> StartProgram(GrblController controller)
    {
        long epoch = controller.CoordinateEpoch;
        Task<bool> first = controller.StartAsync(_phases[0].Gcode);
        return RunPhasesAsync(controller, epoch, first);
    }

    private async Task<bool> RunPhasesAsync(GrblController controller, long epoch, Task<bool> running)
    {
        for (int i = 1; i < _phases.Count; i++)
        {
            if (!await running)
            {
                return false;
            }
            var next = _phases[i];
            var pause = new ToolChangePause(controller, epoch);
            _toolChange = pause;
            _restartRequiresReopen = true;
            ToolChangeNext.Text = "Monta " + next.ToolLabel;
            StatusText.Text = "Pausa cambio utensile. La prossima fase attende la tua conferma.";
            UpdateUi();
            if (!await pause.Resume.Task)
            {
                return false;
            }
            _toolChange = null;
            _preview.SetToolDiameter(next.ToolDiameter);
            _lastElapsed = 0;
            FeedSlider.Value = 100;
            SpindleSlider.Value = 100;
            StatusText.Text = "Ripresa dopo il cambio utensile con «" + next.ToolLabel + "».";
            running = controller.StartAsync(next.Gcode, expectedCoordinateEpoch: epoch);
            UpdateUi();
        }
        return await running;
    }

    private void UpdateToolChangeState(GrblController? controller)
    {
        var pause = _toolChange;
        ToolChangePanel.Visibility = pause == null ? Visibility.Collapsed : Visibility.Visible;
        if (pause == null || pause.Resume.Task.IsCompleted)
        {
            return;
        }
        if (!ReferenceIsValid(pause, controller))
        {
            AbortToolChange("Sequenza interrotta per perdita del riferimento. Verifica gli zeri prima di un nuovo avvio.");
        }
        else if (controller!.SpindleRunning)
        {
            ToolChangeStatus.Text = "In attesa dello spegnimento del mandrino rilevato da GRBL.";
            if (controller.IsJogging || _jogPress != null)
            {
                AbortToolChange("Sequenza interrotta. GRBL segnala il mandrino attivo durante il cambio.");
                _ = EndJogPressAsync(true);
            }
        }
        else if (pause.SettingZeroZ)
        {
            ToolChangeStatus.Text = "Azzeramento Z in corso…";
        }
        else if (pause.ZeroZConfirmed)
        {
            ToolChangeStatus.Text = "Zero Z acquisito. Quando sei pronto, puoi riprendere.";
        }
        else
        {
            ToolChangeStatus.Text = "Dopo il cambio, porta la nuova punta sullo zero Z e confermalo qui sotto.";
        }
    }

    private async void ToolChangeZeroZ_Click(object sender, RoutedEventArgs e)
    {
        var pause = _toolChange;
        if (pause == null || !ToolChangeZeroZButton.IsEnabled || !ManualMotionAllowed(pause.Controller))
        {
            return;
        }
        pause.ZeroZConfirmed = false;
        pause.SettingZeroZ = true;
        UpdateUi();
        try
        {
            await pause.Controller.SetZeroAsync(xy: false);
            if (_toolChange == pause && ReferenceIsValid(pause, _controller))
            {
                pause.ZeroZConfirmed = true;
                StatusText.Text = "Zero Z acquisito per la nuova fresa. Lo zero XY resta sul pezzo.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            pause.SettingZeroZ = false;
            UpdateUi();
        }
    }

    private void ResumeAfterToolChange_Click(object sender, RoutedEventArgs e)
    {
        if (CanResumeAfterToolChange(_controller))
        {
            _toolChange!.Resume.TrySetResult(true);
            UpdateUi();
        }
    }

    private void AbortToolChange(string message)
    {
        if (_toolChange?.Resume.TrySetResult(false) == true)
        {
            StatusText.Text = message + " Per ricominciare, rimonta il primo utensile e riapri CUT.";
        }
    }
}
