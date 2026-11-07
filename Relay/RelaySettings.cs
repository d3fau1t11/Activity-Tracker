using Microsoft.Extensions.Configuration;

namespace Activity_Tracker.Relay;

public sealed record RelaySettings(bool Enabled, string BaseUrl, string DeviceId, string BotToken,
    string ChatId, int PollSeconds = 30, int RetentionDays = 7, int DirectTimeoutSeconds = 8, int RecoverySeconds = 60)
{
    public bool SharedBot { get; init; }
    public string DeviceName { get; init; } = "My PC";
    public string InstanceId { get; init; } = "";
    public string BotId => BotToken.Split(':')[0];
    public RelaySettings ResolveIdentity(string directory)
    {
        if (!Enabled) return this;
        var instance = RelayDeviceIdentity.GetOrCreate(directory);
        return this with { InstanceId = instance, DeviceId = DeviceId == "auto" ? "pc-" + instance : DeviceId };
    }
    public static RelaySettings Read(IConfiguration config)
    {
        var value = new RelaySettings(config.GetValue("Relay:Enabled", false),
            config["Relay:BaseUrl"]?.TrimEnd('/') ?? "", config["Relay:DeviceId"] ?? "auto",
            config["Telegram:BotToken"] ?? "", config["Telegram:ChatId"] ?? "",
            Math.Clamp(config.GetValue("Relay:PollSeconds", 30), 2, 60),
            Math.Clamp(config.GetValue("Relay:LocalRetentionDays", 7), 1, 30),
            Math.Clamp(config.GetValue("Relay:DirectTimeoutSeconds", 8), 6, 30),
            Math.Clamp(config.GetValue("Relay:RecoverySeconds", 60), 30, 600))
        {
            SharedBot = config.GetValue("Relay:SharedBot", true),
            DeviceName = config["Relay:DeviceName"] ?? Environment.MachineName
        };
        if (!value.Enabled) return value;
        if (!Uri.TryCreate(value.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Relay:BaseUrl must be an HTTPS origin, such as https://your-site.netlify.app.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.DeviceId, "^[A-Za-z0-9_-]{3,64}$"))
            throw new InvalidOperationException("Relay:DeviceId must contain 3-64 letters, digits, underscores, or hyphens.");
        if (value.DeviceName.Length is < 1 or > 40 || value.DeviceName.Any(char.IsControl))
            throw new InvalidOperationException("Relay:DeviceName must be a display name of 1-40 characters without control characters.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.BotToken, @"^\d+:[A-Za-z0-9_-]{8,}$")
            || !System.Text.RegularExpressions.Regex.IsMatch(value.ChatId, @"^[0-9]{1,20}$"))
            throw new InvalidOperationException("Configure your Telegram bot token and private user/chat ID for direct access and Netlify fallback.");
        return value;
    }
}
