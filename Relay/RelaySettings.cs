using Microsoft.Extensions.Configuration;

namespace Activity_Tracker.Relay;

public sealed record RelaySettings(bool Enabled, string BaseUrl, string DeviceId, string BotToken,
    string ChatId, int PollSeconds = 30, int RetentionDays = 7, int DirectTimeoutSeconds = 8, int RecoverySeconds = 60)
{
    public string BotId => BotToken.Split(':')[0];
    public static RelaySettings Read(IConfiguration config)
    {
        var value = new RelaySettings(config.GetValue("Relay:Enabled", false),
            config["Relay:BaseUrl"]?.TrimEnd('/') ?? "", config["Relay:DeviceId"] ?? "my-pc",
            config["Telegram:BotToken"] ?? "", config["Telegram:ChatId"] ?? "",
            Math.Clamp(config.GetValue("Relay:PollSeconds", 30), 2, 60),
            Math.Clamp(config.GetValue("Relay:LocalRetentionDays", 7), 1, 30),
            Math.Clamp(config.GetValue("Relay:DirectTimeoutSeconds", 8), 6, 30),
            Math.Clamp(config.GetValue("Relay:RecoverySeconds", 60), 30, 600));
        if (!value.Enabled) return value;
        if (!Uri.TryCreate(value.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Relay:BaseUrl must be an HTTPS origin, such as https://your-site.netlify.app.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.DeviceId, "^[A-Za-z0-9_-]{3,64}$"))
            throw new InvalidOperationException("Relay:DeviceId must contain 3-64 letters, digits, underscores, or hyphens.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.BotToken, @"^\d+:[A-Za-z0-9_-]{8,}$")
            || !System.Text.RegularExpressions.Regex.IsMatch(value.ChatId, @"^[0-9]{1,20}$"))
            throw new InvalidOperationException("Configure your Telegram bot token and private user/chat ID for direct access and Netlify fallback.");
        return value;
    }
}
