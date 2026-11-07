using System.Globalization;

namespace Activity_Tracker.Telegram;

public sealed record ClipboardRequest(string Action = "list", int Page = 0, Guid ItemId = default);

public static class TelegramClipboard
{
    public const int PageSize = 5;
    public const string Usage = "Use /clipboard [page] to view saved text and images, or /clipboard clear to clear history. Collection can only be enabled from the local tray.";
    public static bool IsCommand(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault()?.Split('@')[0].Equals("/clipboard", StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParseCommand(string text, out ClipboardRequest request)
    {
        request = new();
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!IsCommand(text) || parts.Length > 2) return false;
        if (parts.Length == 1) return true;
        if (parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase)) { request = new("clear"); return true; }
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1) return false;
        request = new(Page: page - 1);
        return true;
    }

    public static bool TryParseCallback(string data, out ClipboardRequest request)
    {
        request = new();
        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != "clip") return false;
        if (parts[1] == "list" && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page >= 0)
        { request = new(Page: page); return true; }
        if (parts[1] == "item" && Guid.TryParseExact(parts[2], "N", out var id))
        { request = new("item", ItemId: id); return true; }
        return false;
    }

    public static AnalyticsMessage Build(ClipboardItemInfo[] items, bool enabled, int page)
    {
        var pages = Math.Max(1, (items.Length + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var html = $"<b>Clipboard history</b>\n\n{items.Length}/15 items · Collection {(enabled ? "ON" : "off")}\n" +
            $"Page {page + 1} of {pages} · Newest first\n\n" +
            (items.Length == 0 ? "No saved clipboard items. " : "Select an item to retrieve its text or image. ") +
            "History resets on Windows sign-out or restart.\n\n" +
            (enabled ? "Only new copies are saved. Nothing is sent automatically." : "Enable collection from the Activity Tracker tray menu on the PC.");
        var buttons = new List<AnalyticsButton[]>();
        foreach (var item in items.Skip(page * PageSize).Take(PageSize))
        {
            var detail = item.Kind == ClipboardItemKind.Image ? $"Image · {item.Width}×{item.Height}" : $"Text · {item.Size} characters";
            buttons.Add([new($"{item.CapturedAt:HH:mm:ss} · {detail}", $"clip:item:{item.Id:N}")]);
        }
        var navigation = new List<AnalyticsButton>();
        if (page > 0) navigation.Add(new("‹ Previous", $"clip:list:{page - 1}"));
        if (page + 1 < pages) navigation.Add(new("Next ›", $"clip:list:{page + 1}"));
        if (navigation.Count > 0) buttons.Add(navigation.ToArray());
        buttons.Add([new("Refresh", $"clip:list:{page}"), new("Main menu", TelegramCallbackData.NavMain)]);
        return new(html, buttons.ToArray());
    }
}
