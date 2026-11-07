using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Activity_Tracker.Relay;

public static class RelayRouting
{
    public static string Selector(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant()[..16];

    public static void Decorate(RelaySettings settings, Dictionary<string, JsonElement> fields)
    {
        var selector = Selector(settings.DeviceId);
        if (fields.TryGetValue("reply_markup", out var markup))
        {
            var keyboard = JsonNode.Parse(markup.ValueKind == JsonValueKind.String ? markup.GetString()! : markup.GetRawText());
            if (keyboard?["inline_keyboard"] is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonArray>())
                    foreach (var button in row.OfType<JsonObject>())
                        if (button["callback_data"] is JsonValue data)
                        {
                            var routed = $"d:{selector}:{data.GetValue<string>()}";
                            if (Encoding.UTF8.GetByteCount(routed) > 64) throw new InvalidDataException("Device callback exceeds Telegram's limit.");
                            button["callback_data"] = routed;
                        }
                rows.Add(new JsonArray(new JsonObject { ["text"] = "Choose PC", ["callback_data"] = "pc:list" }));
                fields["reply_markup"] = JsonSerializer.SerializeToElement(keyboard);
            }
        }
        var label = $"[{settings.DeviceName} · {selector[..6]}]";
        var mode = fields.TryGetValue("parse_mode", out var parse) ? parse.ToString() : "";
        if (mode.Equals("HTML", StringComparison.OrdinalIgnoreCase)) label = System.Net.WebUtility.HtmlEncode(label);
        else if (mode.StartsWith("Markdown", StringComparison.OrdinalIgnoreCase))
            label = string.Concat(label.Select(c => "_*[]()~`>#+-=|{}.!\\".Contains(c) ? "\\" + c : c.ToString()));
        foreach (var (key, limit) in new[] { ("text", 4096), ("caption", 1024) })
            if (fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var original = value.GetString()!;
                // Preserve a message already using Telegram's full size allowance.
                if (label.Length + 1 + original.Length <= limit)
                    fields[key] = JsonSerializer.SerializeToElement(label + "\n" + original);
            }
    }
}
