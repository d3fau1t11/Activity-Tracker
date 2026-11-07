using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Activity_Tracker;

public sealed record DigestSettings(bool Enabled, TimeOnly SendAt, bool SendEmptyDays)
{
    public static DigestSettings Read(IConfiguration config)
    {
        var time = config["DailyDigest:SendAt"] ?? "08:00";
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var sendAt))
            throw new InvalidOperationException("DailyDigest:SendAt must use HH:mm (local time).");
        return new(config.GetValue("DailyDigest:Enabled", true), sendAt,
            config.GetValue("DailyDigest:SendEmptyDays", false));
    }

    public DateOnly? DueDate(DateTime now, DateOnly? lastProcessed)
    {
        if (!Enabled || TimeOnly.FromDateTime(now) < SendAt) return null;
        var yesterday = DateOnly.FromDateTime(now).AddDays(-1);
        return lastProcessed.HasValue && lastProcessed.Value >= yesterday ? null : yesterday;
    }
}

public sealed class DailyDigestService(
    IConfiguration config, LocalStorageService storage, TelegramBotService telegram,
    ILogger<DailyDigestService> logger) : BackgroundService
{
    private DateOnly? _processedInThisRun;
    private DateTime _retryAfter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try { await CheckAsync(DateTime.Now, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Daily digest could not be processed; retrying in 15 minutes.");
                    _retryAfter = DateTime.Now.AddMinutes(15);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task CheckAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (now < _retryAfter) return;
        var settings = DigestSettings.Read(config);
        if (!settings.Enabled) return;
        // Read failures deliberately propagate: a corrupt marker must not trigger duplicate sends.
        var last = storage.LoadDigestDate();
        if (_processedInThisRun.HasValue && (!last.HasValue || _processedInThisRun > last))
        {
            storage.SaveDigestDate(_processedInThisRun.Value);
            last = _processedInThisRun;
        }
        var due = settings.DueDate(now, last);
        if (!due.HasValue) return;
        var summary = ActivitySummary.Build(storage.GetReportHistory(), due.Value.ToDateTime(TimeOnly.MinValue));
        if (summary.Seconds > 0 || settings.SendEmptyDays)
        {
            if (!await telegram.SendDailyDigestAsync(Format(summary), cancellationToken, due.Value))
            {
                _retryAfter = now.AddMinutes(15);
                return;
            }
        }
        _processedInThisRun = due;
        storage.SaveDigestDate(due.Value);
        logger.LogInformation("Daily digest processed for {Date}", due);
    }

    public static string Format(ActivityOverview summary)
    {
        var text = new StringBuilder();
        text.AppendLine($"Daily activity · {summary.Date:ddd, dd MMM yyyy}");
        text.AppendLine($"{Environment.MachineName} · {Environment.UserName}");
        text.AppendLine();
        text.AppendLine($"Active time: {summary.Duration}");
        text.AppendLine($"Apps: {summary.Apps.Length} · Sessions: {summary.Sessions.Length}");
        var previous = summary.Days[^2].Seconds;
        if (previous > 0)
        {
            var change = (summary.Seconds - previous) * 100.0 / previous;
            text.AppendLine($"Previous day: {change:+0;-0;0}% active time");
        }
        text.AppendLine();
        text.AppendLine(summary.Apps.Length == 0 ? "No completed activity recorded." : "Top apps");
        foreach (var app in summary.Apps.Take(5))
        {
            var name = app.Name.Replace('\r', ' ').Replace('\n', ' ');
            if (name.Length > 80) name = name[..77] + "...";
            text.AppendLine($"• {name}: {app.Duration}");
        }
        text.AppendLine();
        text.Append("Completed sessions only · Local calendar day");
        return text.ToString();
    }
}
