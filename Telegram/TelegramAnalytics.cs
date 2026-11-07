using System.Globalization;
using System.Net;
using System.Text;

namespace Activity_Tracker.Telegram;

public sealed record AnalyticsRequest(DateTime Date, string View = "summary", int Page = 0)
{
    public string Callback => $"an:{View}:{Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:{Page.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record AnalyticsButton(string Text, string Callback);
public sealed record AnalyticsMessage(string Html, AnalyticsButton[][] Buttons);

/// <summary>Read-only bot views. Parsing and rendering are independent of Telegram transport.</summary>
public static class TelegramAnalytics
{
    public const int SessionsPerPage = 5;
    public const string Usage = "Use /analytics [YYYY-MM-DD], /trends [YYYY-MM-DD], or /sessions [YYYY-MM-DD] [page]. Dates use the PC's local calendar; future dates are not supported.";
    private static readonly DateTime EarliestDay = DateTime.MinValue.AddDays(6);

    private static string[] Parts(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static string Command(string part) => part.Split('@')[0].ToLowerInvariant();

    public static bool IsCommand(string text) => Parts(text).FirstOrDefault() is { } first
        && Command(first) is "/analytics" or "/trends" or "/sessions";

    public static bool TryParseCommand(string text, DateTime today, out AnalyticsRequest request)
    {
        request = new(today.Date);
        var parts = Parts(text);
        if (parts.Length == 0 || !IsCommand(text)) return false;
        var view = Command(parts[0]) switch { "/trends" => "trends", "/sessions" => "sessions", _ => "summary" };
        if (parts.Length > (view == "sessions" ? 3 : 2)) return false;
        var date = today.Date;
        if (parts.Length >= 2 && !DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date)) return false;
        var page = 1;
        if (parts.Length == 3 && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1)) return false;
        if (!ValidDate(date, today)) return false;
        request = new(date.Date, view, page - 1);
        return true;
    }

    public static bool TryParseCallback(string data, DateTime today, out AnalyticsRequest request)
    {
        request = new(today.Date);
        if (data == TelegramCallbackData.Analytics) return true;
        var parts = data.Split(':');
        if (parts.Length != 4 || parts[0] != "an" || !ValidView(parts[1])) return false;
        var date = today.Date;
        if (parts[2] != "today" && !DateTime.TryParseExact(parts[2], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        if (!ValidDate(date, today)) return false;
        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 0) return false;
        if (parts[1] != "sessions" && page != 0) return false;
        request = new(date, parts[1], page);
        return true;
    }

    private static bool ValidDate(DateTime date, DateTime today) => date >= EarliestDay && date.Date <= today.Date;
    private static bool ValidView(string view) => view is "summary" or "trends" or "sessions";

    public static AnalyticsMessage Build(IEnumerable<ActivityReport> reports, AnalyticsRequest request,
        DateTime today, Func<string, string> filterTitle)
    {
        if (!ValidDate(request.Date, today) || !ValidView(request.View) || request.Page < 0)
            throw new ArgumentException("Invalid analytics request.", nameof(request));
        var summary = ActivitySummary.Build(reports, request.Date);
        var pageCount = Math.Max(1, (summary.Sessions.Length + SessionsPerPage - 1) / SessionsPerPage);
        request = request with { Page = request.View == "sessions" ? Math.Min(request.Page, pageCount - 1) : 0 };
        var text = new StringBuilder();
        var title = request.View switch { "trends" => "Seven-day trend", "sessions" => "Session history", _ => "Activity overview" };
        text.AppendLine($"<b>{title} · {request.Date:dd MMM yyyy}</b>");
        text.AppendLine();

        if (request.View == "summary")
        {
            text.AppendLine($"<b>Active time:</b> {summary.Duration}");
            text.AppendLine($"<b>Apps:</b> {summary.Apps.Length} · <b>Sessions:</b> {summary.Sessions.Length}");
            text.AppendLine();
            text.AppendLine(summary.Apps.Length == 0 ? "No completed sessions on this day." : "<b>Top apps</b>");
            foreach (var app in summary.Apps.Take(5))
                text.AppendLine($"• {Html(app.Name, 80)} — {app.Duration}");
        }
        else if (request.View == "trends")
        {
            text.AppendLine($"{summary.Days[0].Date:dd MMM} – {request.Date:dd MMM yyyy}");
            text.AppendLine($"<b>Total:</b> {ActivitySummary.FormatDuration(summary.Days.Sum(d => d.Seconds))}");
            text.AppendLine("<pre>");
            var maximum = Math.Max(1, summary.Days.Max(d => d.Seconds));
            foreach (var day in summary.Days)
            {
                var length = day.Seconds == 0 ? 0 : Math.Max(1, (int)Math.Round(day.Seconds * 10.0 / maximum));
                text.AppendLine($"{day.Date:ddd dd} {new string('█', length).PadRight(10, '·')} {day.Duration}");
            }
            text.AppendLine("</pre>");
        }
        else
        {
            text.AppendLine($"Page {request.Page + 1} of {pageCount} · {summary.Sessions.Length} sessions · Most recent first");
            if (summary.Sessions.Length == 0) text.AppendLine("No completed sessions on this day.");
            foreach (var session in summary.Sessions.Skip(request.Page * SessionsPerPage).Take(SessionsPerPage))
            {
                text.AppendLine();
                text.AppendLine($"<b>{session.Time}</b> · {session.Duration}");
                text.AppendLine(Html(session.AppName, 48));
                var filtered = filterTitle(session.Title);
                if (!string.IsNullOrWhiteSpace(filtered)) text.AppendLine(Html(filtered, 80));
            }
        }
        text.AppendLine();
        text.Append("<i>Completed sessions · PC local time · Tap Refresh to update</i>");

        var buttons = new List<AnalyticsButton[]>
        {
            new[] { Button("Overview", request with { View = "summary", Page = 0 }),
             Button("Trends", request with { View = "trends", Page = 0 }),
             Button("Sessions", request with { View = "sessions", Page = 0 }) }
        };
        if (request.View == "sessions" && pageCount > 1)
        {
            var pages = new List<AnalyticsButton>();
            if (request.Page > 0) pages.Add(Button("‹ Previous page", request with { Page = request.Page - 1 }));
            if (request.Page + 1 < pageCount) pages.Add(Button("Next page ›", request with { Page = request.Page + 1 }));
            buttons.Add(pages.ToArray());
        }
        var dates = new List<AnalyticsButton>();
        if (request.Date > EarliestDay) dates.Add(Button("‹ Previous day", request with { Date = request.Date.AddDays(-1), Page = 0 }));
        dates.Add(new("Today", $"an:{request.View}:today:0"));
        if (request.Date.Date < today.Date) dates.Add(Button("Next day ›", request with { Date = request.Date.AddDays(1), Page = 0 }));
        buttons.Add(dates.ToArray());
        buttons.Add([Button("Refresh", request), new("Main menu", TelegramCallbackData.NavMain)]);
        return new(text.ToString(), buttons.ToArray());
    }

    private static AnalyticsButton Button(string text, AnalyticsRequest request) => new(text, request.Callback);

    private static string Html(string value, int limit)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        if (value.Length > limit)
        {
            if (char.IsHighSurrogate(value[limit - 1])) limit--;
            value = value[..limit] + "…";
        }
        return WebUtility.HtmlEncode(value);
    }
}
