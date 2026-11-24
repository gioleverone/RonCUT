using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using RonCUT;

internal static class NotificationTests
{
    private const string Code = "G21\nG90\nM5\nM30\n";
    private static GeneralSettings Settings() => new GeneralSettings
    {
        CommunicationsApiUrl = "http://example.invalid/api/telegram/?op=send&token=a%2Bb%26c&text=Avvio%20CUT"
    };

    public static async Task Run(Action<bool, string> check)
    {
        using (var f = await Fixture.Create())
        {
            f.Transport.StatusState = "Run";
            f.Controller.QueryStatus();
            f.Transport.StatusState = "Idle";
            f.Controller.QueryStatus();
            Task<bool> run = f.Notifications.RunAsync(f.Controller, Code, false, Settings());
            await WaitUntil(() => f.Handler.Requests.Count == 1);
            check(!run.IsCompleted && f.Handler.Requests.Count == 1, "notifiche: START inviato, fine in attesa dello stato Idle successivo alle ultime righe");
            bool completed = await Finish(f.Controller, run);
            await f.Notifications.PendingNotifications.WaitAsync(TimeSpan.FromSeconds(3));
            var requests = f.Handler.Requests.ToArray();
            check(completed && requests.Length == 2 && requests.All(r => r.Method == HttpMethod.Get) &&
                requests[0].Url == Settings().CommunicationsApiUrl + "&msg=%F0%9F%AA%B5%20CNC%20Start%20%F0%9F%9B%A0%EF%B8%8F" &&
                requests[1].Url == Settings().CommunicationsApiUrl + "&msg=%F0%9F%AA%B5%20CNC%20End%20%E2%9C%85",
                "notifiche: due GET sullo stesso URL con parametri originali ed emoji UTF-8");
        }

        using (var f = await Fixture.Create())
        {
            bool completed = await Finish(f.Controller, f.Notifications.RunAsync(f.Controller, Code, true, Settings()));
            await f.Notifications.PendingNotifications;
            check(completed && f.Handler.Requests.IsEmpty, "notifiche: nessuna chiamata in simulazione");
        }

        using (var f = await Fixture.Create())
        {
            bool completed = await Finish(f.Controller, f.Notifications.RunAsync(f.Controller, Code, false, new GeneralSettings()));
            await f.Notifications.PendingNotifications;
            check(completed && f.Handler.Requests.IsEmpty, "notifiche: il campo vuoto disattiva le chiamate");
        }

        foreach (string query in new[] { "", "?msg=vecchio&%6Dsg=duplicato" })
        {
            using var f = await Fixture.Create();
            const string endpoint = "https://example.invalid/comunicazioni";
            var settings = new GeneralSettings { CommunicationsApiUrl = endpoint + query + "#locale" };
            await Finish(f.Controller, f.Notifications.RunAsync(f.Controller, Code, false, settings));
            await f.Notifications.PendingNotifications.WaitAsync(TimeSpan.FromSeconds(3));
            var requests = f.Handler.Requests.Select(r => r.Url).ToArray();
            check(requests.Length == 2 && requests.All(url => url.StartsWith(endpoint + "?msg=") && !url.Contains('&') && !url.Contains('#')),
                query == "" ? "notifiche: aggiunta della query a un URL senza parametri" : "notifiche: sostituzione del parametro msg preesistente, anche se codificato");
        }

        using (var f = await Fixture.Create())
        {
            f.Controller.Dispose();
            bool rejected = false;
            try
            {
                await f.Notifications.RunAsync(f.Controller, Code, false, Settings());
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            check(rejected && f.Handler.Requests.IsEmpty, "notifiche: un avvio rifiutato non invia START");
        }

        foreach (string interruption in new[] { "STOP", "errore GRBL", "disconnessione" })
        {
            using var f = await Fixture.Create();
            f.Transport.AutoAck = false;
            Task<bool> run = f.Notifications.RunAsync(f.Controller, Code, false, Settings());
            await WaitUntil(() => f.Handler.Requests.Count == 1);
            if (interruption == "STOP")
            {
                f.Controller.Stop();
            }
            else if (interruption == "errore GRBL")
            {
                f.Transport.Reply("error:33");
            }
            else
            {
                f.Transport.LoseConnection();
            }
            bool completed = await run.WaitAsync(TimeSpan.FromSeconds(3));
            await f.Notifications.PendingNotifications.WaitAsync(TimeSpan.FromSeconds(3));
            check(!completed && f.Handler.Requests.Count == 1, "notifiche: " + interruption + " non produce una falsa notifica di completamento");
        }

        using (var f = await Fixture.Create())
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Handler.Respond = async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };
            Task<bool> run = f.Notifications.RunAsync(f.Controller, Code, false, Settings());
            await WaitUntil(() => !f.Handler.Requests.IsEmpty);
            bool completed = await Finish(f.Controller, run);
            check(completed && !f.Notifications.PendingNotifications.IsCompleted, "notifiche: una risposta HTTP sospesa non blocca l'esecuzione né la conclusione CNC");
            release.SetResult();
            await f.Notifications.PendingNotifications.WaitAsync(TimeSpan.FromSeconds(3));
            check(f.Handler.Requests.Count == 2, "notifiche: fine accodata dopo la risposta START senza duplicazioni");
        }

        foreach (string failure in new[] { "HTTP", "rete", "timeout" })
        {
            using var f = await Fixture.Create(failure == "timeout" ? TimeSpan.FromMilliseconds(120) : null);
            var messages = new ConcurrentQueue<string>();
            f.Notifications.StatusChanged += messages.Enqueue;
            f.Handler.Respond = async (_, token) =>
            {
                if (failure == "timeout")
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                if (failure == "rete")
                {
                    throw new HttpRequestException("URL https://example.invalid/?token=segreto");
                }
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            };
            bool completed = await Finish(f.Controller, f.Notifications.RunAsync(f.Controller, Code, false, Settings()));
            await f.Notifications.PendingNotifications.WaitAsync(TimeSpan.FromSeconds(3));
            check(completed && string.IsNullOrEmpty(f.Controller.LastError) && !f.Transport.Realtime.Contains(0x18) && f.Handler.Requests.Count == 2, "notifiche: errore " + failure + " isolato dalla CNC, nessuna ripetizione automatica");
            check(messages.Any(m => m.Contains("Nessun nuovo tentativo")) && messages.All(m => !m.Contains("segreto") && !m.Contains("https://")), "notifiche: segnalazione " + failure + " senza esporre URL o token");
        }
    }

    private static async Task<bool> Finish(GrblController controller, Task<bool> run)
    {
        await WaitUntil(() =>
        {
            controller.QueryStatus();
            return run.IsCompleted;
        });
        return await run;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Verifica delle notifiche non completata.");
            }
            await Task.Delay(10);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, string Url)> Requests { get; } = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.Method, request.RequestUri!.AbsoluteUri));
            return Respond(request, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public GrblTests.ControlledTransport Transport { get; } = new();
        public GrblController Controller { get; }
        public RecordingHandler Handler { get; } = new();
        public HttpClient Client { get; }
        public CutNotificationService Notifications { get; }

        private Fixture(TimeSpan? timeout)
        {
            Controller = new GrblController(Transport);
            Client = new HttpClient(Handler);
            Notifications = new CutNotificationService(Client, timeout);
        }

        public static async Task<Fixture> Create(TimeSpan? timeout = null)
        {
            var fixture = new Fixture(timeout);
            await fixture.Controller.ConnectAsync();
            fixture.Controller.QueryStatus();
            return fixture;
        }

        public void Dispose()
        {
            Controller.Dispose();
            Client.Dispose();
        }
    }
}
