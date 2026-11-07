namespace Activity_Tracker;

/// <summary>Shared, deterministic calendar-day calculations for bot analytics and the digest.</summary>
public static class ActivitySummary
{
    public static ActivityOverview Build(IEnumerable<ActivityReport> reports, DateTime selectedDate)
    {
        var day = selectedDate.Date;
        var unique = reports.DistinctBy(r => r.Id).Where(r => r.DurationSeconds > 0).ToArray();
        var sessions = ForDay(unique, day);
        var apps = sessions.GroupBy(s => s.AppName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppTotal(g.Key, g.Sum(s => s.Seconds)))
            .OrderByDescending(a => a.Seconds).ThenBy(a => a.Name).ToArray();
        var days = Enumerable.Range(-6, 7).Select(offset =>
        {
            var date = day.AddDays(offset);
            return new DayTotal(date, ForDay(unique, date).Sum(s => s.Seconds));
        }).ToArray();
        return new ActivityOverview(day, sessions.Sum(s => s.Seconds), apps, sessions, days);
    }

    private static SessionTotal[] ForDay(IEnumerable<ActivityReport> reports, DateTime day)
    {
        var tomorrow = day.AddDays(1);
        return reports.Select(report =>
        {
            // Reject malformed durations instead of letting one record break the whole view.
            if (report.Timestamp > DateTime.MaxValue.AddSeconds(-report.DurationSeconds)) return null;
            var end = report.Timestamp.AddSeconds(report.DurationSeconds);
            var start = report.Timestamp < day ? day : report.Timestamp;
            end = end > tomorrow ? tomorrow : end;
            if (start >= end) return null;
            return new SessionTotal(start, end, report.AppName, report.WindowTitle,
                (long)(end - start).TotalSeconds);
        }).OfType<SessionTotal>().OrderByDescending(s => s.Start).ToArray();
    }

    public static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60}m";
    }
}

public sealed record ActivityOverview(DateTime Date, long Seconds, AppTotal[] Apps,
    SessionTotal[] Sessions, DayTotal[] Days)
{
    public string Duration => ActivitySummary.FormatDuration(Seconds);
    public string TopApp => Apps.FirstOrDefault()?.Name ?? "—";
}

public sealed record AppTotal(string Name, long Seconds)
{
    public string Duration => ActivitySummary.FormatDuration(Seconds);
}

public sealed record DayTotal(DateTime Date, long Seconds)
{
    public string Label => Date.ToString("ddd");
    public string Duration => ActivitySummary.FormatDuration(Seconds);
}

public sealed record SessionTotal(DateTime Start, DateTime End, string AppName, string Title, long Seconds)
{
    public string Time => $"{Start:HH:mm} – {End:HH:mm}";
    public string Duration => ActivitySummary.FormatDuration(Seconds);
}
