using Activity_Tracker.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Telegram.Bot;

internal static partial class Program
{
    private static async Task CheckMultiDeviceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ActivityTrackerMultiChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identityDirectory = Path.Combine(directory, "identity-one");
            var identities = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RelayDeviceIdentity.GetOrCreate(identityDirectory))));
            Expect(identities.Distinct().Count() == 1, "Concurrent startup preserves one stable PC identity.");
            var secondIdentity = RelayDeviceIdentity.GetOrCreate(Path.Combine(directory, "identity-two"));
            Expect(secondIdentity != identities[0], "A second PC gets a distinct identity even with the same preset configuration.");
            var preset = RelaySettings.Read(Config(new() { ["Relay:Enabled"] = "true", ["Relay:BaseUrl"] = "https://relay.fixture.test",
                ["Telegram:BotToken"] = "123:fixture-only-token", ["Telegram:ChatId"] = "123" }));
            var one = preset.ResolveIdentity(identityDirectory) with { DeviceName = "Office <PC>", PollSeconds = 0 };
            var two = preset.ResolveIdentity(Path.Combine(directory, "identity-two")) with { DeviceName = "Laptop", PollSeconds = 0 };
            Expect(preset.SharedBot && preset.DeviceId == "auto" && one.DeviceId != two.DeviceId, "Shared bot mode resolves automatic IDs per installation.");
            Expect(one.DeviceId == preset.ResolveIdentity(identityDirectory).DeviceId, "Restarts and copied app settings preserve the saved identity.");
            Expect(RelayRouting.Selector("pc-one") == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pc-one"))).ToLowerInvariant()[..16],
                "Device selectors use the same SHA256 format as the cloud router.");
            var wire = new SharedBotWire();
            using var direct = new HttpClient(wire, false);
            using var relay = new HttpClient(wire, false);
            using var transportOne = new RelayTransport(one, new RelayOutbox(Path.Combine(directory, "one"), b => b, b => b), NullLogger<RelayTransport>.Instance, direct, relay);
            using var transportTwo = new RelayTransport(two, new RelayOutbox(Path.Combine(directory, "two"), b => b, b => b), NullLogger<RelayTransport>.Instance,
                new HttpClient(wire, false), new HttpClient(wire, false));
            using var clientOne = new HttpClient(new RelayHttpHandler(transportOne));
            using var clientTwo = new HttpClient(new RelayHttpHandler(transportTwo));
            var botOne = new TelegramBotClient(one.BotToken, clientOne);
            var botTwo = new TelegramBotClient(two.BotToken, clientTwo);
            var commands = await Task.WhenAll(botOne.GetUpdates(offset: 0), botTwo.GetUpdates(offset: 0));
            Expect(commands.All(batch => batch.Length == 1) && wire.PolledDevices.Distinct().Count() == 2,
                "Two actual SDK clients poll separate authenticated Netlify queues.");
            Expect(wire.DirectCalls == 0, "Shared PCs never compete for Telegram getUpdates.");
            Expect(wire.PolledInstances.Contains(one.InstanceId) && wire.PolledInstances.Contains(two.InstanceId), "Each queue request carries its installation identity.");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/botfixture/sendMessage")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { chat_id = "123", text = "Fixture reply", parse_mode = "HTML",
                    reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Screenshot", callback_data = "cmd:screenshot" } } } } }), Encoding.UTF8, "application/json")
            };
            using (var response = await clientOne.SendAsync(request)) Expect(response.IsSuccessStatusCode, "Shared PCs retain direct Telegram delivery for healthy replies.");
            var fields = wire.LastDirect!.RootElement;
            Expect(fields.GetProperty("text").GetString()!.Contains("Office &lt;PC&gt;"), "Device labels respect HTML escaping.");
            var keyboard = fields.GetProperty("reply_markup").GetProperty("inline_keyboard");
            Expect(keyboard[0][0].GetProperty("callback_data").GetString() == $"d:{RelayRouting.Selector(one.DeviceId)}:cmd:screenshot",
                "Menu actions carry their originating PC instead of the current global selection.");
            Expect(keyboard[1][0].GetProperty("callback_data").GetString() == "pc:list", "Menus expose the central PC selector.");
            var before = wire.DirectCalls;
            await botOne.AnswerCallbackQuery("fixture-callback");
            Expect(wire.DirectCalls == before, "PC acknowledgements do not compete with the webhook's prompt acknowledgement.");
            Expect(wire.PolledDevices.Count == 2, "Healthy replies do not pass through Netlify.");
            var maximum = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(new string('x', 4096)) };
            RelayRouting.Decorate(one, maximum);
            Expect(maximum["text"].GetString()!.Length == 4096, "Device labels preserve messages already at Telegram's limit.");
            var invalid = new Dictionary<string, JsonElement> { ["reply_markup"] = JsonSerializer.SerializeToElement(new {
                inline_keyboard = new[] { new[] { new { callback_data = new string('x', 46) } } } }) };
            try { RelayRouting.Decorate(one, invalid); Expect(false, "Oversized callback rejected."); }
            catch (InvalidDataException) { Expect(true, "Device callback payloads stay within Telegram's 64-byte limit."); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class SharedBotWire : HttpMessageHandler
    {
        public int DirectCalls;
        public List<string> PolledDevices { get; } = [];
        public List<string> PolledInstances { get; } = [];
        public JsonDocument? LastDirect;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = await request.Content!.ReadAsStringAsync(ct);
            if (request.RequestUri!.Host == "api.telegram.org")
            {
                DirectCalls++;
                LastDirect = JsonDocument.Parse(text);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":42}}", Encoding.UTF8, "application/json") };
            }
            Expect(request.RequestUri.Host == "relay.fixture.test" && request.Headers.Authorization?.ToString() == "Bearer 123:fixture-only-token",
                "Shared command traffic uses only the authenticated HTTPS relay.");
            using var body = JsonDocument.Parse(text);
            Expect(body.RootElement.GetProperty("method").GetString() == "getUpdates", "Only PC-specific command polls use the fixture relay.");
            PolledDevices.Add(body.RootElement.GetProperty("deviceId").GetString()!);
            PolledInstances.Add(body.RootElement.GetProperty("instanceId").GetString()!);
            var result = JsonSerializer.Serialize(new { ok = true, result = new[] { new { update_id = 1, message = new {
                message_id = 1, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), text = "/status",
                chat = new { id = 123, type = "private" }, from = new { id = 123, first_name = "Owner", is_bot = false }
            } } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }
}
