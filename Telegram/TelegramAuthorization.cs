using System.Globalization;
using Telegram.Bot.Types.Enums;

namespace Activity_Tracker.Telegram;

/// <summary>Commands are restricted to the configured owner in a private chat.</summary>
public static class TelegramAuthorization
{
    public static bool IsFresh(DateTime sentAt, DateTime nowUtc) =>
        sentAt >= nowUtc.AddMinutes(-2) && sentAt <= nowUtc.AddSeconds(30);

    public static bool IsOwnerChat(string configuredChatId, string chatId) =>
        long.TryParse(configuredChatId, NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
        && owner > 0 && string.Equals(configuredChatId, chatId, StringComparison.Ordinal);

    public static bool IsAuthorized(string configuredChatId, long chatId, long actorId, ChatType chatType) =>
        chatType == ChatType.Private && actorId == chatId
        && IsOwnerChat(configuredChatId, chatId.ToString(CultureInfo.InvariantCulture));
}
