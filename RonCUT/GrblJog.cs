using System.Globalization;
using System.IO;

namespace RonCUT;

public sealed partial class GrblController
{
    private CancellationTokenSource? _jogCancellation;
    private Task _jogTask = Task.CompletedTask;
    private bool _jogCancelRequested;
    private bool _jogCancelNeedsRepeat;
    private bool _jogResetRequested;
    private Exception? _jogFailure;
    private DateTime _jogHoldUntil;
    public bool IsJogging => _jogCancellation != null || (IsConnected && !_desynced && State == "Jog");
    public bool IsJogCancelling => _jogCancellation != null && _jogCancelRequested;

    private Task BeginJog(char axis, int direction, double distance, double feed, bool continuous)
    {
        if (axis is not ('X' or 'Y' or 'Z') || direction is not (-1 or 1) ||
            !double.IsFinite(distance) || distance <= 0 || distance > 1000 ||
            !double.IsFinite(feed) || feed <= 0 || feed > 100000)
        {
            throw new InvalidOperationException("Profilo di movimento non valido.");
        }
        string command = "$J=G91G21" + axis + (direction * distance).ToString("0.####", CultureInfo.InvariantCulture) + "F" + feed.ToString("0.###", CultureInfo.InvariantCulture);
        TaskCompletionSource<bool> completion;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            EnsureIdle();
            (completion, cancellation) = CreateJogSession();
        }
        _ = ExecuteJogAsync(command, continuous, cancellation, completion);
        Changed?.Invoke();
        return completion.Task;
    }

    private (TaskCompletionSource<bool>, CancellationTokenSource) CreateJogSession()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        _jogTask = completion.Task;
        _jogCancellation = cancellation;
        _jogCancelRequested = false;
        _jogCancelNeedsRepeat = false;
        _jogResetRequested = false;
        _jogFailure = null;
        _manualPending = true;
        LastError = "";
        _jogHoldUntil = DateTime.UtcNow.AddMilliseconds(600);
        return (completion, cancellation);
    }

    public Task CancelJogAsync()
    {
        Task result;
        TaskCompletionSource<bool>? completion = null;
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Lo STOP manuale non è disponibile durante il programma CUT.");
            }
            if (_jogCancellation == null)
            {
                if (State != "Jog")
                {
                    return Task.CompletedTask;
                }
                if (!IsConnected || !_identified || !_handshakeComplete || _manualPending || _desynced)
                {
                    throw new InvalidOperationException("Collegamento non pronto per annullare il movimento manuale.");
                }
                (completion, cancellation) = CreateJogSession();
            }
            RequestJogCancel();
            result = _jogTask;
        }
        if (completion != null && cancellation != null)
        {
            _ = ExecuteJogAsync(null, false, cancellation, completion);
        }
        Changed?.Invoke();
        return result;
    }

    private void RequestJogCancel()
    {
        lock (_gate)
        {
            if (!_jogCancelRequested)
            {
                _jogCancelRequested = true;
                _jogCancelNeedsRepeat = _pending != null;
                try
                {
                    if (IsConnected)
                    {
                        _transport.Write(new byte[] { 0x85 });
                    }
                }
                catch (Exception ex)
                {
                    _desynced = true;
                    _jogFailure = ex;
                    _pending?.TrySetException(ex);
                    throw;
                }
                finally
                {
                    // I callback di cancellazione possono riprendere subito il runner: il byte deve essere già partito.
                    _jogCancellation?.Cancel();
                }
            }
        }
    }

    public void RenewJogHold()
    {
        lock (_gate)
        {
            if (_jogCancellation != null && !_jogCancelRequested)
            {
                _jogHoldUntil = DateTime.UtcNow.AddMilliseconds(600);
            }
        }
    }

    private async Task ExecuteJogAsync(string? command, bool continuous, CancellationTokenSource cancellation, TaskCompletionSource<bool> completion)
    {
        Exception? failure = null;
        CancellationToken token = cancellation.Token;
        Task watchdog = continuous ? WatchJogHoldAsync(cancellation) : Task.CompletedTask;
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                CheckJogStatus();
                // L'annullamento blocca nuovi invii ma non abbandona l'ACK già in volo.
                await SendCommandAsync(command!, CancellationToken.None, false, token, false).ConfigureAwait(false);
                QueryStatus();
                if (continuous)
                {
                    await Task.Delay(60, token).ConfigureAwait(false);
                }
            }
            while (continuous);
            await WaitForJogIdleAsync(token, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_jogResetRequested || _jogFailure != null)
            {
                failure = ex;
            }
        }
        try
        {
            failure ??= _jogFailure;
            if ((token.IsCancellationRequested || failure != null) && !_jogResetRequested)
            {
                RequestJogCancel();
                lock (_gate)
                {
                    if (_jogCancelNeedsRepeat && IsConnected)
                    {
                        // Il primo cancel può arrivare quando il jog non è ancora stato interpretato.
                        _transport.Write(new byte[] { 0x85 });
                        _jogCancelNeedsRepeat = false;
                    }
                }
                // Dopo l'ultimo ACK, G4P0 è la barriera di sincronizzazione prevista da GRBL.
                await SendCommandAsync("G4P0", CancellationToken.None, false, resetOnTimeout: false).ConfigureAwait(false);
                await WaitForJogIdleAsync(CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure ??= ex;
            lock (_gate)
            {
                _desynced = true;
                _statusTime = DateTime.MinValue;
            }
        }
        cancellation.Cancel();
        await watchdog.ConfigureAwait(false);
        failure ??= _jogFailure;
        lock (_gate)
        {
            _jogCancellation = null;
            _manualPending = false;
            _jogCancelRequested = false;
            if (failure != null)
            {
                LastError = "Movimento manuale interrotto: " + failure.Message;
            }
        }
        cancellation.Dispose();
        if (failure != null)
        {
            Log?.Invoke(LastError);
            completion.TrySetException(failure);
        }
        else
        {
            completion.TrySetResult(true);
        }
        Changed?.Invoke();
    }

    private async Task WatchJogHoldAsync(CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            while (true)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_jogCancellation != cancellation)
                    {
                        return;
                    }
                    if (DateTime.UtcNow > _jogHoldUntil)
                    {
                        RequestJogCancel();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _desynced = true;
                _jogFailure = ex;
                cancellation.Cancel();
                _pending?.TrySetException(ex);
            }
        }
    }

    private void CheckJogStatus()
    {
        lock (_gate)
        {
            if (!IsConnected || _desynced)
            {
                throw new IOException("Comunicazione CNC non disponibile: riconnetti e verifica la posizione.");
            }
            if (State is not ("Idle" or "Jog"))
            {
                throw new InvalidOperationException("GRBL ha interrotto il movimento manuale: " + State + ".");
            }
            if (DateTime.UtcNow - _statusTime > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Nessun aggiornamento di stato durante il movimento manuale.");
            }
        }
    }

    private async Task WaitForJogIdleAsync(CancellationToken token, TimeSpan? timeout)
    {
        DateTime started = DateTime.UtcNow;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            QueryStatus();
            CheckJogStatus();
            lock (_gate)
            {
                if (State == "Idle" && WorkPosition != null && _statusTime > started)
                {
                    return;
                }
            }
            if (timeout.HasValue && DateTime.UtcNow - started > timeout.Value)
            {
                throw new TimeoutException("Arresto manuale non confermato: verifica la CNC e riconnetti.");
            }
            await Task.Delay(80, token).ConfigureAwait(false);
        }
    }
}
