using System.Net.Http;

namespace RonCUT;

public sealed class CutNotificationService
{
    private static readonly HttpClient SharedClient = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    public event Action<string>? StatusChanged;
    public Task PendingNotifications { get; private set; } = Task.CompletedTask;

    public CutNotificationService(HttpClient? client = null, TimeSpan? timeout = null)
    {
        _client = client ?? SharedClient;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public Task<bool> RunAsync(GrblController controller, string gcode, bool simulation, GeneralSettings settings)
        => RunAsync(() => controller.StartAsync(gcode), simulation, settings);

    public async Task<bool> RunAsync(Func<Task<bool>> start, bool simulation, GeneralSettings settings)
    {
        settings.Validate();
        string baseUrl = settings.CommunicationsApiUrl?.Trim() ?? "";
        bool notificationsEnabled = !simulation && baseUrl.Length > 0;
        // StartAsync rifiuta sincronicamente gli avvii non consentiti: nessuna notifica in quel caso.
        Task<bool> run = start();
        Task startNotification = notificationsEnabled ? SendAsync(baseUrl, "🪵 CNC Start 🛠️") : Task.CompletedTask;
        PendingNotifications = startNotification;
        bool completed = await run.ConfigureAwait(false);
        if (completed && notificationsEnabled)
        {
            PendingNotifications = CompleteNotificationAsync(startNotification, baseUrl);
        }
        return completed;
    }

    private async Task CompleteNotificationAsync(Task startNotification, string baseUrl)
    {
        // Mantiene l'ordine START/fine anche se una lavorazione termina prima della risposta HTTP.
        await startNotification.ConfigureAwait(false);
        await SendAsync(baseUrl, "🪵 CNC End ✅").ConfigureAwait(false);
    }

    private static Uri BuildRequestUri(string baseUrl, string message)
    {
        var uri = new UriBuilder(baseUrl) { Fragment = "" };
        string query = uri.Query.Length == 0 ? "" : uri.Query[1..];
        var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter => Uri.UnescapeDataString(parameter.Split('=', 2)[0]) != "msg")
            .Append("msg=" + Uri.EscapeDataString(message));
        uri.Query = string.Join("&", parameters);
        return uri.Uri;
    }

    private Task SendAsync(string baseUrl, string name)
    {
        // Anche la preparazione della richiesta e l'accesso alla rete rimangono fuori dal thread WPF.
        return Task.Run(async () =>
        {
            Report("Notifica " + name + ": invio in corso…");
            try
            {
                using var timeout = new CancellationTokenSource(_timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(baseUrl, name));
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                Report(response.IsSuccessStatusCode
                    ? "Notifica " + name + ": inviata."
                    : "Notifica " + name + ": risposta HTTP " + (int)response.StatusCode + ". Nessun nuovo tentativo.");
            }
            catch (OperationCanceledException)
            {
                Report("Notifica " + name + ": tempo scaduto. Nessun nuovo tentativo.");
            }
            catch (Exception)
            {
                // I messaggi delle eccezioni possono includere URL con token: non vengono mostrati.
                Report("Notifica " + name + ": invio non riuscito. Nessun nuovo tentativo.");
            }
        });
    }

    private void Report(string message)
    {
        try
        {
            StatusChanged?.Invoke(message);
        }
        catch (Exception)
        {
            // Una finestra chiusa o un errore di visualizzazione non deve propagarsi alla lavorazione.
        }
    }
}
