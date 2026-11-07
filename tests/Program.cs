using Activity_Tracker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Activity_Tracker.Telegram;
using System.Text;
using System.Xml.Linq;

internal static partial class Program
{
    private static int _assertions;

    public static int Main(string[] args)
    {
        try
        {
            CheckSummary();
            CheckSecurityAsync().GetAwaiter().GetResult();
            CheckTimingAndSchedule();
            CheckStorageAndDeliveryAsync().GetAwaiter().GetResult();
            CheckTelegramAnalytics();
            CheckTrackingAndReportingAsync().GetAwaiter().GetResult();
            CheckClipboardHistory();
            CheckRelayAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_assertions} assertions. No live clipboard reads, tracking, or Telegram sends were performed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Expect(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception(message);
    }

    private static ActivityReport Report(DateTime start, int seconds, string app = "code.exe") =>
        new() { Timestamp = start, DurationSeconds = seconds, AppName = app, WindowTitle = "Fixture window" };

    private static void CheckSummary()
    {
        var day = new DateTime(2026, 11, 6);
        var overnight = Report(day.AddMinutes(-10), 1200);
        var afternoon = Report(day.AddHours(14), 1800, "CODE.exe");
        var browser = Report(day.AddHours(15), 300, "msedge.exe");
        var data = new[] { overnight, overnight.Clone(), afternoon, browser,
            Report(day, -1), Report(day.AddDays(1), 600), Report(DateTime.MaxValue, int.MaxValue) };
        var view = ActivitySummary.Build(data, day);
        Expect(view.Seconds == 2700, "Summary clips crossing-midnight sessions and deduplicates report IDs.");
        Expect(view.Sessions.Length == 3, "Summary excludes invalid and next-day records.");
        Expect(view.Apps.Length == 2 && view.Apps[0].Seconds == 2400, "App grouping is case insensitive.");
        Expect(view.Days.Length == 7 && view.Days[0].Date == day.AddDays(-6), "Chart covers exactly seven calendar days.");
        Expect(view.Days[^2].Seconds == 600, "Previous day receives its share of an overnight session.");
        Expect(view.Sessions[0].Start > view.Sessions[1].Start, "Timeline is newest first.");
        Expect(ActivitySummary.Build([], day).Seconds == 0, "Empty days have zero activity.");
        Expect(ActivitySummary.FormatDuration(90061) == "25h 1m", "Durations do not wrap after 24 hours.");
        Expect(!DailyDigestService.Format(view).Contains("Fixture window"), "Digests omit window titles.");
    }

    private static void CheckTimingAndSchedule()
    {
        var start = new DateTime(2026, 11, 6, 9, 0, 0);
        Expect(SessionTiming.EndTime(start, start.AddMinutes(3)) == start.AddMinutes(3), "Idle end uses last input rather than detection time.");
        Expect(SessionTiming.EndTime(start, start.AddMinutes(-1)) == start, "Idle end cannot create negative durations.");
        var schedule = new DigestSettings(true, new TimeOnly(8, 0), false);
        var date = new DateOnly(2026, 11, 5);
        Expect(schedule.DueDate(start.Date.AddHours(7), null) == null, "No digest before scheduled local time.");
        Expect(schedule.DueDate(start.Date.AddHours(8), null) == date, "At scheduled time, summarize yesterday.");
        Expect(schedule.DueDate(start, date) == null, "Already-processed day is not repeated.");
        Expect(schedule.DueDate(start, date.AddDays(2)) == null, "A backward clock change does not resend a day.");
        Expect(schedule.DueDate(start.AddDays(5), date) == date.AddDays(5), "After downtime only the latest complete day is due.");
        Expect((schedule with { Enabled = false }).DueDate(start, null) == null, "Disabled digest never becomes due.");
        try
        {
            DigestSettings.Read(Config(new() { ["DailyDigest:SendAt"] = "25:99" }));
            Expect(false, "Invalid digest time must be rejected.");
        }
        catch (InvalidOperationException) { Expect(true, "Invalid digest time rejected."); }
    }

    private static IConfigurationRoot Config(Dictionary<string, string?>? additional = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "fixture-token",
            ["Telegram:ChatId"] = "123",
            ["DailyDigest:Enabled"] = "true",
            ["DailyDigest:SendAt"] = "08:00"
        };
        foreach (var item in additional ?? []) values[item.Key] = item.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task CheckStorageAndDeliveryAsync()
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ActivityTrackerChecks"));
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storage = new LocalStorageService(storageDirectory: directory);
            var now = new DateTime(2026, 11, 6, 9, 0, 0);
            var report = Report(now.Date.AddDays(-1).AddHours(12), 3600, "app_[name]&.exe");
            storage.Save(report);
            var failing = Report(now, 60);
            failing.RecordSendAttempt(false);
            storage.Save(failing);
            Expect(storage.GetPendingReports(10, readyOnly: true).Count == 1, "Recent failure waits for backoff while a new report is eligible.");
            Expect(storage.GetPendingReports(10).Count == 2, "Pending status still includes reports waiting for backoff.");
            failing.LastSendAttempt = DateTime.Now.AddMinutes(-6);
            storage.Save(failing);
            Expect(storage.GetPendingReports(10, readyOnly: true).Count == 2, "Failed report becomes eligible when its backoff expires.");
            failing.SendAttempts = 10;
            storage.Save(failing);
            Expect(storage.GetPendingReports(10, readyOnly: true).Count == 1, "Exhausted reports are not retried.");
            storage.MoveToFailed(failing);
            report.Sent = true;
            storage.ArchiveReport(report);
            File.WriteAllText(Path.Combine(directory, "active_reports_fixture.json"), JsonSerializer.Serialize(new[] { report }));
            Expect(storage.GetReportHistory().Count == 2, "History spans sent, failed, and rotated queues without duplicate IDs.");

            var handler = new FakeTelegramHandler { Succeed = false };
            using var http = new HttpClient(handler);
            var config = Config();
            var telegram = new TelegramBotService(config, NullLogger<TelegramBotService>.Instance, http);
            var digest = new DailyDigestService(config, storage, telegram, NullLogger<DailyDigestService>.Instance);
            await digest.CheckAsync(now, default);
            Expect(handler.Count == 1 && storage.LoadDigestDate() == null, "Delivery failure does not advance the persisted date.");
            await digest.CheckAsync(now.AddMinutes(1), default);
            Expect(handler.Count == 1, "Digest failures wait 15 minutes before retry.");
            handler.Succeed = true;
            await digest.CheckAsync(now.AddMinutes(15), default);
            Expect(handler.Count == 2 && storage.LoadDigestDate() == new DateOnly(2026, 11, 5), "Successful retry persists the completed date.");
            using var payload = JsonDocument.Parse(handler.LastPayload!);
            Expect(!payload.RootElement.TryGetProperty("parse_mode", out _), "Digest sends plain text regardless of app-name punctuation.");
            Expect(payload.RootElement.GetProperty("text").GetString()!.Contains("app_[name]&.exe"), "App-name punctuation is preserved as text.");
            var restarted = new DailyDigestService(config, storage, telegram, NullLogger<DailyDigestService>.Instance);
            await restarted.CheckAsync(now.AddHours(1), default);
            Expect(handler.Count == 2, "Restart does not resend yesterday's successful digest.");
            config["DailyDigest:Enabled"] = "false";
            await restarted.CheckAsync(now.AddDays(1), default);
            Expect(handler.Count == 2, "Configuration can disable digest delivery without restarting.");
            config["DailyDigest:Enabled"] = "true";
            await restarted.CheckAsync(now.AddDays(2), default);
            Expect(handler.Count == 2 && storage.LoadDigestDate() == new DateOnly(2026, 11, 7), "Empty days are skipped and remembered by default.");
            config["DailyDigest:SendEmptyDays"] = "true";
            await restarted.CheckAsync(now.AddDays(3), default);
            Expect(handler.Count == 3, "Empty-day digest can be enabled.");
            File.WriteAllText(Path.Combine(directory, "daily_digest.json"), "corrupted fixture");
            try { await restarted.CheckAsync(now.AddDays(4), default); Expect(false, "Corrupt marker must block delivery."); }
            catch (JsonException) { Expect(handler.Count == 3, "Corrupt marker does not trigger another send."); }

            var recoveryDirectory = Path.Combine(directory, "marker-recovery");
            var recoveryStorage = new LocalStorageService(storageDirectory: recoveryDirectory);
            var markerPath = Path.Combine(recoveryDirectory, "daily_digest.json");
            Directory.CreateDirectory(markerPath); // Force marker persistence to fail after delivery.
            var recovery = new DailyDigestService(config, recoveryStorage, telegram, NullLogger<DailyDigestService>.Instance);
            try { await recovery.CheckAsync(now, default); Expect(false, "An unwritable marker must report failure."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Expect(handler.Count == 4, "Delivery happened before the marker write failed."); }
            Directory.Delete(markerPath);
            await recovery.CheckAsync(now.AddMinutes(15), default);
            Expect(handler.Count == 4 && recoveryStorage.LoadDigestDate() == new DateOnly(2026, 11, 5),
                "Within one run, a failed marker write is retried without resending the digest.");
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean up outside the test directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CheckTelegramAnalytics()
    {
        var day = new DateTime(2026, 11, 6);
        Expect(TelegramAnalytics.TryParseCommand("/analytics", day, out var request) && request.Date == day, "Analytics defaults to today.");
        Expect(TelegramAnalytics.TryParseCommand("/trends@fixture_bot 2026-11-05", day, out request)
            && request.View == "trends" && request.Date == day.AddDays(-1), "Dated commands support the bot suffix.");
        Expect(TelegramAnalytics.TryParseCommand("/sessions 2026-11-05 2", day, out request)
            && request.Page == 1, "Session command converts human page numbers correctly.");
        foreach (var invalid in new[] { "/analytics 2026-02-30", "/analytics 2026-11-07", "/sessions 2026-11-06 0",
            "/sessions 2026-11-06 -1", "/sessions 2026-11-06 999999999999", "/analytics 2026-11-05 extra", "/analytics 0001-01-01" })
            Expect(!TelegramAnalytics.TryParseCommand(invalid, day, out _), $"Reject invalid command: {invalid}");
        Expect(!TelegramAnalytics.IsCommand("/analyticsomething"), "Unrelated commands do not enter the analytics handler.");
        Expect(TelegramAnalytics.TryParseCallback(TelegramCallbackData.Analytics, day, out request) && request.Date == day,
            "Main-menu Analytics button opens today.");
        foreach (var invalid in new[] { "an:unknown:20261106:0", "an:sessions:20261107:0", "an:sessions:20261106:-1", "an:trends:20261106:3" })
            Expect(!TelegramAnalytics.TryParseCallback(invalid, day, out _), "Malformed or future callbacks are rejected.");

        var reports = Enumerable.Range(0, 12).Select(i => Report(day.AddHours(8).AddMinutes(i * 10), 600, "App <&>.exe")).ToArray();
        foreach (var report in reports) report.WindowTitle = "<b>Fixture</b> & title";
        var overview = TelegramAnalytics.Build(reports, new(day), day, title => title);
        Expect(overview.Html.Contains("2h 0m") && overview.Html.Contains("12"), "Bot overview uses the shared activity totals.");
        Expect(overview.Html.Contains("App &lt;&amp;&gt;.exe"), "Overview escapes app names for Telegram HTML.");
        Expect(!overview.Html.Contains("Fixture"), "Overview omits individual session titles.");
        Expect(!overview.Buttons.SelectMany(b => b).Any(b => b.Text == "Next day ›"), "Today never offers a future-day button.");
        var todayButton = overview.Buttons.SelectMany(b => b).Single(b => b.Text == "Today");
        Expect(TelegramAnalytics.TryParseCallback(todayButton.Callback, day.AddDays(1), out request) && request.Date == day.AddDays(1),
            "Today buttons in older messages resolve the current day when pressed.");

        var first = TelegramAnalytics.Build(reports, new(day, "sessions"), day, title => title);
        var next = first.Buttons.SelectMany(b => b).Single(b => b.Text == "Next page ›");
        Expect(TelegramAnalytics.TryParseCallback(next.Callback, day, out request) && request.Page == 1,
            "Session pagination round-trips through callback parsing.");
        var second = TelegramAnalytics.Build(reports, request, day, title => title);
        Expect(first.Html.Contains("09:50") && !second.Html.Contains("09:50") && second.Html.Contains("09:00"),
            "The next page advances through distinct sessions in descending order.");
        var last = TelegramAnalytics.Build(reports, new(day, "sessions", int.MaxValue), day, title => title);
        Expect(last.Html.Contains("Page 3 of 3") && !last.Buttons.SelectMany(b => b).Any(b => b.Text == "Next page ›"),
            "An out-of-range page clamps to the final page without overflowing.");
        var empty = TelegramAnalytics.Build([], new(day, "sessions", 20), day, title => title);
        Expect(empty.Html.Contains("Page 1 of 1") && empty.Html.Contains("No completed sessions"), "Empty history has a useful single page.");
        var trends = TelegramAnalytics.Build(reports, new(day, "trends"), day, title => title);
        Expect(trends.Html.Contains("31 Oct") && trends.Html.Contains("2h 0m") && trends.Html.Contains('█'),
            "Seven-day view includes the correct range, total, and chart.");

        foreach (var view in new[] { overview, first, second, last, empty, trends })
        {
            Expect(XElement.Parse("<root>" + view.Html + "</root>").Value.Length < 4096, "Bot view is valid markup within Telegram's text limit.");
            foreach (var button in view.Buttons.SelectMany(row => row))
                Expect(Encoding.UTF8.GetByteCount(button.Callback) <= 64
                    && (button.Callback == TelegramCallbackData.NavMain || TelegramAnalytics.TryParseCallback(button.Callback, day, out _)),
                    "Every generated button is a valid, bounded callback.");
        }
        using var http = new HttpClient(new FakeTelegramHandler());
        var filter = new TelegramBotService(Config(), NullLogger<TelegramBotService>.Instance, http);
        var sensitive = Report(day.AddHours(12), 60);
        sensitive.WindowTitle = "password manager — private entry";
        var privateView = TelegramAnalytics.Build([sensitive], new(day, "sessions"), day, filter.FormatWindowTitle);
        Expect(!privateView.Html.Contains("private entry") && privateView.Html.Contains("Sensitive Content"),
            "Session history applies the outgoing report's sensitive-title filter.");
        using var privateHttp = new HttpClient(new FakeTelegramHandler());
        var hidden = new TelegramBotService(Config(new() { ["Telegram:EnablePrivacyMode"] = "true" }),
            NullLogger<TelegramBotService>.Instance, privateHttp);
        Expect(!TelegramAnalytics.Build(reports, new(day, "sessions"), day, hidden.FormatWindowTitle).Html.Contains("Fixture"),
            "Privacy mode hides all session titles in bot analytics.");
        using var hashHttp = new HttpClient(new FakeTelegramHandler());
        var hashed = new TelegramBotService(Config(new() { ["Privacy:HashWindowTitles"] = "true" }),
            NullLogger<TelegramBotService>.Instance, hashHttp);
        Expect(!TelegramAnalytics.Build(reports, new(day, "sessions"), day, hashed.FormatWindowTitle).Html.Contains("Fixture"),
            "Hashed-title mode also applies to bot analytics.");
        foreach (var report in reports) { report.WindowTitle = new string('<', 3000); report.AppName = new string('&', 3000); }
        var longView = TelegramAnalytics.Build(reports, new(day, "sessions"), day, title => title);
        Expect(longView.Html.Length < 4096 && XElement.Parse("<root>" + longView.Html + "</root>").Value.Contains('…'),
            "Long titles and application names are safely truncated before HTML encoding.");
    }

    private sealed class FakeTelegramHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public bool Succeed { get; set; }
        public string? LastPayload { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            LastPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Succeed ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent(Succeed ? "{\"ok\":true}" : "{\"ok\":false}") };
        }
    }
}
