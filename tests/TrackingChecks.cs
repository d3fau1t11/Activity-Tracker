using Activity_Tracker;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

internal static partial class Program
{
    private static ActivitySessionTracker Tracker() => new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(30));

    private static async Task CheckTrackingAndReportingAsync()
    {
        var start = new DateTime(2026, 11, 6, 9, 0, 0);
        var tracker = Tracker();
        var saved = new List<ActivityReport>();
        tracker.Observe(start, "code.exe", "A", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(20), "code.exe", "A", TimeSpan.Zero);
        Expect(tracker.SavePending(saved.Add) == 0, "Paused tracking records no sessions.");
        tracker.Start();
        tracker.Observe(start, "code.exe", "A", TimeSpan.Zero);
        tracker.Start();
        tracker.Observe(start.AddSeconds(20), "code.exe", "B", TimeSpan.Zero);
        tracker.Stop(start.AddSeconds(40));
        tracker.Stop(start.AddSeconds(50));
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 2 && saved.Sum(r => r.DurationSeconds) == 40, "Title changes split sessions and repeated start/stop does not duplicate activity.");
        Expect(saved.Select(r => r.WindowTitle).SequenceEqual(new[] { "A", "B" }), "Session reports preserve the observed title.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        tracker.Observe(start, "short.exe", "", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(14), "kept.exe", "", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(29), "", "", TimeSpan.Zero);
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 1 && saved[0].AppName == "kept.exe" && saved[0].DurationSeconds == 15,
            "The minimum-duration boundary is exact and an ignored window closes active usage.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        for (var second = 0; second <= 210; second += 10)
            tracker.Observe(start.AddSeconds(second), "code.exe", "idle", TimeSpan.FromSeconds(Math.Max(0, second - 20)));
        Expect(tracker.IsIdle, "Crossing the idle threshold sets the tracking status to idle.");
        tracker.Observe(start.AddSeconds(220), "code.exe", "returned", TimeSpan.Zero);
        tracker.Stop(start.AddSeconds(240));
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 2 && saved.Sum(r => r.DurationSeconds) == 40, "Idle time is excluded and activity resumes as a fresh session.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        var midnight = start.Date.AddDays(1);
        tracker.Observe(midnight.AddSeconds(-20), "code.exe", "night", TimeSpan.Zero);
        tracker.Observe(midnight, "code.exe", "night", TimeSpan.Zero);
        tracker.Stop(midnight.AddSeconds(20));
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 2 && saved[0].DurationSeconds == 20 && saved[1].Timestamp == midnight,
            "Midnight splits a continuing session without gaps or overlap.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        tracker.Observe(start, "code.exe", "before sleep", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(20), "code.exe", "before sleep", TimeSpan.Zero);
        tracker.Observe(start.AddHours(1), "code.exe", "after sleep", TimeSpan.Zero);
        tracker.Stop(start.AddHours(1).AddSeconds(20));
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 2 && saved.Sum(r => r.DurationSeconds) == 40, "A long interruption in observations is not counted as activity.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        tracker.Observe(start, "code.exe", "missed idle", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(20), "code.exe", "missed idle", TimeSpan.FromSeconds(5));
        tracker.Observe(start.AddSeconds(220), "code.exe", "missed idle", TimeSpan.FromSeconds(205));
        tracker.SavePending(saved.Add);
        Expect(saved.Single().DurationSeconds == 15, "Known idle time takes precedence over the last poll after an observation gap.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        tracker.Observe(start, "code.exe", "switch", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(20), "", "", TimeSpan.Zero, sessionActive: false);
        tracker.Observe(start.AddHours(1), "", "", TimeSpan.Zero, sessionActive: false);
        tracker.Observe(start.AddHours(1).AddSeconds(10), "code.exe", "returned", TimeSpan.Zero);
        tracker.Stop(start.AddHours(1).AddSeconds(30));
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 2 && saved.Sum(r => r.DurationSeconds) == 40, "Inactive Windows sessions do not accrue tracking time.");

        tracker = Tracker(); saved.Clear(); tracker.Start();
        tracker.Observe(start, "code.exe", "clock", TimeSpan.Zero);
        tracker.Observe(start.AddSeconds(20), "code.exe", "clock", TimeSpan.Zero);
        tracker.Observe(start.AddHours(-1), "code.exe", "clock", TimeSpan.Zero);
        tracker.Stop(start.AddHours(-1).AddSeconds(20));
        Guid unsavedId = default;
        try { tracker.SavePending(r => { unsavedId = r.Id; throw new IOException("Fixture disk full"); }); }
        catch (IOException) { }
        Expect(tracker.UnsavedCount == 1, "A failed disk write retains a completed report in memory.");
        tracker.SavePending(saved.Add);
        Expect(saved.Count == 1 && saved[0].Id == unsavedId && saved[0].DurationSeconds == 20,
            "Disk recovery retries the same report ID and a backward clock change does not duplicate time.");

        using var directory = new FixtureDirectory();
        var storage = new LocalStorageService(storageDirectory: directory.Path);
        tracker = Tracker(); tracker.Start();
        tracker.Observe(start, "a_`<&>.exe", "C:\\work\\notes * [draft]", TimeSpan.Zero);
        tracker.Stop(start.AddSeconds(20));
        tracker.SavePending(storage.Save);
        var handler = new DeliveryHandler { Status = HttpStatusCode.ServiceUnavailable };
        handler.BeforeReply = _ =>
        {
            Expect(storage.LoadAll().Count > 0, "Reports are durable before their first HTTP request.");
            return Task.CompletedTask;
        };
        using var http = new HttpClient(handler);
        var telegram = new TelegramBotService(Config(), NullLogger<TelegramBotService>.Instance, http);
        var delivery = new ReportDeliveryService(storage, telegram, NullLogger<ReportDeliveryService>.Instance);
        Expect(await delivery.FlushAsync() == 0, "Transport failure leaves the activity unsent.");
        Expect(storage.LoadAll().Single().SendAttempts == 1 && !storage.LoadAll().Single().Sent, "Failure and retry state are saved.");
        await delivery.FlushAsync();
        Expect(handler.Count == 1, "Immediate flush does not bypass report backoff.");
        var pending = storage.LoadAll().Single();
        pending.LastSendAttempt = DateTime.Now.AddMinutes(-6);
        storage.Save(pending);
        handler.Status = HttpStatusCode.OK;
        Expect(await delivery.FlushAsync() == 1 && storage.LoadAll().Count == 0, "An eligible retry sends and archives the completed session.");
        Expect(storage.GetReportHistory().Single().DurationSeconds == 20, "Tracking duration survives storage, retry, and archive unchanged.");
        await delivery.FlushAsync();
        Expect(handler.Count == 2, "Successfully archived reports are not resent.");
        using (var payload = JsonDocument.Parse(handler.LastPayload!))
        {
            Expect(payload.RootElement.GetProperty("parse_mode").GetString() == "HTML", "Activity reports use safe HTML formatting.");
            var plain = XElement.Parse("<root>" + payload.RootElement.GetProperty("text").GetString() + "</root>").Value;
            Expect(plain.Contains("a_`<&>.exe") && plain.Contains("C:\\work\\notes * [draft]") && plain.Contains("2026-11-06"),
                "Special characters, backslashes, and dates remain intact in the delivered report.");
        }

        handler.BeforeReply = null;
        storage.Save(Report(start.AddHours(2), 30));
        var concurrent = await Task.WhenAll(delivery.FlushAsync(), delivery.FlushAsync());
        Expect(concurrent.Sum() == 1 && handler.Count == 3, "Concurrent flush requests serialize without duplicate delivery.");

        await CheckDeliveryFailureRecoveryAsync(start);
        await CheckPayloadResponsesAsync(start);
        CheckQueueRecovery(start);
    }

    private static async Task CheckDeliveryFailureRecoveryAsync(DateTime start)
    {
        using var directory = new FixtureDirectory();
        var storage = new LocalStorageService(storageDirectory: directory.Path);
        var handler = new DeliveryHandler();
        using var http = new HttpClient(handler);
        var telegram = new TelegramBotService(Config(), NullLogger<TelegramBotService>.Instance, http);
        var delivery = new ReportDeliveryService(storage, telegram, NullLogger<ReportDeliveryService>.Instance);
        storage.Save(Report(start, 60));
        var blocker = System.IO.Path.Combine(directory.Path, "sent_reports.json.tmp");
        Directory.CreateDirectory(blocker);
        try { await delivery.FlushAsync(); Expect(false, "Archive write failure must be surfaced."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Expect(storage.LoadAll().Single().Sent, "The sent receipt survives an archive-write failure.");
        Directory.Delete(blocker);
        var restarted = new ReportDeliveryService(storage, telegram, NullLogger<ReportDeliveryService>.Instance);
        await restarted.FlushAsync();
        Expect(handler.Count == 1 && storage.LoadAll().Count == 0 && storage.GetReportHistory().Count == 1,
            "Restart completes archiving without sending a second Telegram message.");

        var exhausted = Report(start.AddHours(1), 60);
        exhausted.SendAttempts = 9;
        exhausted.LastSendAttempt = DateTime.Now.AddDays(-2);
        storage.Save(exhausted);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        blocker = System.IO.Path.Combine(directory.Path, "failed_reports.json.tmp");
        Directory.CreateDirectory(blocker);
        try { await restarted.FlushAsync(); Expect(false, "Failed-queue write error must be surfaced."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Expect(storage.LoadAll().Single().SendAttempts == 10, "Exhausted report remains durable until failed-archive write succeeds.");
        Directory.Delete(blocker);
        await restarted.FlushAsync();
        Expect(handler.Count == 2 && storage.GetStatistics().FailedCount == 1 && storage.LoadAll().Count == 0,
            "Exhausted reports move to failed storage without an eleventh network attempt.");

        var cancellable = Report(start.AddHours(2), 60);
        storage.Save(cancellable);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeReply = async token => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var cancellation = new CancellationTokenSource();
        var operation = restarted.FlushAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        try { await operation; Expect(false, "In-flight cancellation must propagate."); }
        catch (OperationCanceledException) { }
        Expect(storage.LoadAll().Single().SendAttempts == 0, "Cancellation does not consume a send attempt or remove the report.");
        handler.BeforeReply = null;
        handler.Status = HttpStatusCode.OK;
        Expect(await restarted.FlushAsync() == 1, "The delivery gate remains usable after cancellation.");
    }

    private static async Task CheckPayloadResponsesAsync(DateTime start)
    {
        var handler = new DeliveryHandler();
        using var http = new HttpClient(handler);
        var telegram = new TelegramBotService(Config(new() { ["Telegram:MaxMessageLength"] = "1" }), NullLogger<TelegramBotService>.Instance, http);
        var report = Report(start, 120, new string('&', 300));
        report.WindowTitle = string.Concat(Enumerable.Repeat("<>&🟢", 2000));
        report.UserName = new string('<', 300);
        Expect(await telegram.SendReportAsync(report), "Long report fields send successfully with a clamped message limit.");
        using var payload = JsonDocument.Parse(handler.LastPayload!);
        var text = payload.RootElement.GetProperty("text").GetString();
        Expect(XElement.Parse("<root>" + text + "</root>").Value.Length <= 512, "Truncation preserves valid markup and the configured bounded text length.");
        handler.Body = "{\"ok\":false}";
        Expect(!await telegram.SendReportAsync(report), "HTTP 200 without Telegram confirmation is not recorded as delivered.");
        handler.Body = "not json";
        Expect(!await telegram.SendReportAsync(report), "Malformed Telegram responses leave reports retryable.");
    }

    private static void CheckQueueRecovery(DateTime start)
    {
        using var directory = new FixtureDirectory();
        var legacy = Report(start, 60);
        var path = System.IO.Path.Combine(directory.Path, "active_reports_legacy.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { legacy }));
        var storage = new LocalStorageService(storageDirectory: directory.Path);
        Expect(storage.GetPendingReports(10).Single().Id == legacy.Id, "Previously rotated offline reports rejoin the delivery queue.");
        Expect(!File.Exists(path) && Directory.GetFiles(directory.Path, "*.recovered").Length == 1,
            "Legacy input is retained as a backup after the merged queue is saved.");
        storage.GetPendingReports(10);
        Expect(storage.LoadAll().Count == 1, "Queue recovery is idempotent.");
        var missingAttemptTime = storage.LoadAll().Single();
        missingAttemptTime.SendAttempts = 1;
        missingAttemptTime.LastSendAttempt = null;
        storage.Save(missingAttemptTime);
        Expect(storage.GetPendingReports(10, readyOnly: true).Count == 1,
            "Legacy retry records with a missing attempt timestamp do not remain stuck forever.");
        var oversized = Report(start, 60);
        oversized.WindowTitle = new string('x', 11 * 1024 * 1024);
        storage.Save(oversized);
        Expect(storage.GetPendingReports(10).Count == 2 && Directory.GetFiles(directory.Path, "active_reports_*.json").Length == 0,
            "A queue over 10 MB stays reachable instead of being rotated away.");
        var activePath = System.IO.Path.Combine(directory.Path, "active_reports.json");
        File.WriteAllText(activePath, "corrupt fixture");
        try { storage.Save(Report(start, 60)); Expect(false, "Corrupt report files must not be treated as empty queues."); }
        catch (JsonException) { }
        Expect(File.ReadAllText(activePath) == "corrupt fixture" && File.Exists(activePath + ".corrupted"),
            "Corrupt storage is preserved and backed up without silently replacing its contents.");
        storage.PerformMaintenance();
        Expect(File.ReadAllText(activePath) == "corrupt fixture", "Maintenance also preserves corrupt source data.");
    }

    private sealed class DeliveryHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{\"ok\":true}";
        public int Count { get; private set; }
        public string? LastPayload { get; private set; }
        public Func<CancellationToken, Task>? BeforeReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            LastPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (BeforeReply != null) await BeforeReply(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }

    private sealed class FixtureDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ActivityTrackerChecks"));
        public string Path { get; }
        public FixtureDirectory() { Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
