using Activity_Tracker.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Telegram.Bot;
using Telegram.Bot.Types;

internal static partial class Program
{
    private static async Task CheckRelayAsync()
    {
        const string token = "123:fixture-only-token";
        Expect(!RelaySettings.Read(Config()).Enabled, "Existing installations retain ordinary direct Telegram when fallback is disabled.");
        var free = RelaySettings.Read(Config(new() {
            ["Relay:Enabled"] = "true", ["Relay:BaseUrl"] = "https://relay.fixture.test",
            ["Telegram:BotToken"] = token, ["Telegram:ChatId"] = "123" }));
        Expect(free.BotId == "123" && free.PollSeconds == 30 && free.DirectTimeoutSeconds == 8,
            "Netlify fallback requires only the existing bot token, private chat, and site URL.");
        try { RelaySettings.Read(Config(new() { ["Relay:Enabled"] = "true", ["Relay:BaseUrl"] = "http://insecure.test" })); Expect(false, "HTTP relay rejected."); }
        catch (InvalidOperationException) { Expect(true, "The relay origin must be HTTPS."); }
        var directory = Path.Combine(Path.GetTempPath(), "ActivityTrackerRelayChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var outbox = new RelayOutbox(directory, b => b.Select(x => (byte)(x ^ 91)).ToArray(), b => b.Select(x => (byte)(x ^ 91)).ToArray());
            var settings = new RelaySettings(true, "https://relay.fixture.test", "fixture-pc", token, "123", PollSeconds: 0);
            var now = DateTimeOffset.UtcNow;
            var f = new FallbackWire(token);
            using var direct = new HttpClient(f, false);
            using var relay = new HttpClient(f, false);
            using var transport = new RelayTransport(settings, outbox, NullLogger<RelayTransport>.Instance, direct, relay, () => now);
            using var client = new HttpClient(new RelayHttpHandler(transport));
            var bot = new TelegramBotClient(token, client);
            async Task<HttpResponseMessage> SendReport(string id, string text = "Sensitive fixture text")
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/botfixture/sendMessage")
                    { Content = new StringContent(JsonSerializer.Serialize(new { chat_id = "123", text }), Encoding.UTF8, "application/json") };
                request.Headers.Add("X-Activity-Delivery-Id", id);
                return await client.SendAsync(request);
            }
            var healthy = Guid.NewGuid().ToString("N");
            using (var response = await SendReport(healthy)) Expect(response.IsSuccessStatusCode, "Healthy delivery reaches Telegram directly.");
            var directBefore = f.DirectCalls;
            using (var response = await SendReport(healthy, "Changed fixture")) Expect(response.IsSuccessStatusCode && f.DirectCalls == directBefore,
                "A completed direct report uses its encrypted receipt instead of sending twice.");
            Expect(f.RelayCalls == 0, "Healthy Telegram delivery consumes no Netlify requests.");
            using var bitmap = new SKBitmap(16, 12);
            bitmap.Erase(new SKColor(12, 98, 210));
            using var image = SKImage.FromBitmap(bitmap);
            using var pngData = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = pngData.ToArray();
            var webp = WebpImages.Convert(png, true);
            using var decoded = SKBitmap.Decode(webp);
            Expect(Encoding.ASCII.GetString(webp, 8, 4) == "WEBP" && decoded.GetPixel(2, 2) == bitmap.GetPixel(2, 2),
                "Actual lossless WebP preserves screenshot pixels.");
            using (var stream = new MemoryStream(png)) await bot.SendPhoto(123, InputFile.FromStream(stream, "screenshot_fixture.png"));
            Expect(f.DirectUploadType == "image/jpeg" && f.RelayCalls == 0, "Direct photo mode creates a JPEG preview from the WebP.");
            using (var stream = new MemoryStream(png)) await bot.SendDocument(123, InputFile.FromStream(stream, "clipboard-fixture.png"));
            Expect(f.DirectUploadType == "image/webp" && Encoding.ASCII.GetString(f.DirectUpload!, 8, 4) == "WEBP",
                "Direct documents preserve actual WebP image bytes.");
            using (var poll = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/botfixture/getUpdates")
                { Content = new StringContent("{\"offset\":0,\"timeout\":30}", Encoding.UTF8, "application/json") })
            using (var response = await client.SendAsync(poll))
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var updates = body.RootElement.GetProperty("result");
                Expect(updates[0].TryGetProperty("message", out _) && !updates[1].TryGetProperty("message", out _)
                    && !updates[2].TryGetProperty("message", out _), "Direct polling keeps only fresh commands from the configured private actor.");
                Expect(updates[1].GetProperty("update_id").GetInt32() == 11, "Filtered updates retain IDs so polling offsets advance.");
            }
            f.DirectAvailable = false; f.RelayAvailable = false;
            var outage = Guid.NewGuid().ToString("N");
            using (var response = await SendReport(outage)) Expect(response.StatusCode == HttpStatusCode.ServiceUnavailable && outbox.List().Length == 1,
                "A failure of both routes retains the encrypted local delivery.");
            var disk = File.ReadAllBytes(Path.Combine(directory, outage + ".relay"));
            Expect(!Encoding.UTF8.GetString(disk).Contains("Sensitive fixture text"), "Pending report content is encrypted on disk.");
            var reopened = new RelayOutbox(directory, b => b.Select(x => (byte)(x ^ 91)).ToArray(), b => b.Select(x => (byte)(x ^ 91)).ToArray());
            Expect(reopened.List()[0].Id == outage && reopened.GetReceipt(outage, now)!.Relayed,
                "Delivery content and relay ownership survive a restart.");
            f.RelayAvailable = true; f.Queued = true;
            using (var response = await SendReport(outage))
                Expect(response.StatusCode == HttpStatusCode.Accepted && outbox.List().Length == 0,
                    "Durable Netlify acceptance transfers retry ownership without claiming Telegram success.");
            f.DirectAvailable = true; now = now.AddMinutes(2); f.Queued = false;
            var beforeCloudReplay = f.DirectCalls;
            using (var response = await SendReport(outage, "Changed after recovery"))
                Expect(response.IsSuccessStatusCode && f.DirectCalls == beforeCloudReplay,
                    "A cloud-owned report queries Netlify after recovery and cannot be sent directly twice.");
            using (var response = await SendReport(Guid.NewGuid().ToString("N")))
                Expect(response.IsSuccessStatusCode && f.DirectCalls > beforeCloudReplay, "New requests return to direct Telegram after the recovery interval.");
            f.DirectAvailable = false;
            using (var stream = new MemoryStream(png)) await bot.SendPhoto(123, InputFile.FromStream(stream, "screenshot_fallback.png"));
            Expect(f.UploadCount == 1 && f.LastAuthorization == "Bearer " + token,
                "Actual SDK image fallback uses the existing bot token over HTTPS.");
            Expect(f.LastEnvelope!.RootElement.GetProperty("files")[0].GetProperty("path").GetString()!
                .StartsWith("devices/fixture-pc/uploads/"), "Netlify image references are scoped to the configured device.");
            using var largeBitmap = new SKBitmap(768, 768);
            var random = new Random(123);
            largeBitmap.Pixels = Enumerable.Range(0, 768 * 768).Select(_ => new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))).ToArray();
            using var largeImage = SKImage.FromBitmap(largeBitmap);
            using var largePng = largeImage.Encode(SKEncodedImageFormat.Png, 100);
            var largeId = Guid.NewGuid().ToString("N");
            async Task<HttpResponseMessage> SendLarge()
            {
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent("123"), "chat_id");
                multipart.Add(new ByteArrayContent(largePng.ToArray()), "photo", "screenshot_large.png");
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/botfixture/sendPhoto") { Content = multipart };
                request.Headers.Add("X-Activity-Delivery-Id", largeId);
                return await client.SendAsync(request);
            }
            f.FailedPart = 1;
            using (var response = await SendLarge()) Expect(response.StatusCode == HttpStatusCode.ServiceUnavailable && outbox.List().Length == 1,
                "An interrupted image upload stays encrypted locally.");
            f.FailedPart = null;
            using (var response = await SendLarge()) Expect(response.IsSuccessStatusCode && outbox.List().Length == 0,
                "An interrupted image resumes under its original delivery ID.");
            Expect(f.LargestUpload <= 1024 * 1024 && f.MultiChunkCompleted && f.AllOriginsAllowed,
                "Large real WebP files use bounded authenticated chunks and no Firebase requests.");
            var lostId = Guid.NewGuid().ToString("N");
            f.LoseRelayResponse = true;
            using (var response = await SendReport(lostId))
                Expect(response.StatusCode == HttpStatusCode.ServiceUnavailable && outbox.List().Length == 1,
                    "A lost reply after cloud acceptance retains the local envelope and relay ownership.");
            f.LoseRelayResponse = false; f.DirectAvailable = true; now = now.AddMinutes(2);
            var beforeLostReplay = f.DirectCalls;
            using (var response = await SendReport(lostId))
                Expect(response.IsSuccessStatusCode && f.DirectCalls == beforeLostReplay && outbox.List().Length == 0,
                    "A lost cloud reply is resolved by querying its original ID instead of sending again after recovery.");
            var oldRecipientId = Guid.NewGuid().ToString("N");
            outbox.Save(new(oldRecipientId, "sendMessage", new() {
                ["chat_id"] = JsonSerializer.SerializeToElement("456"), ["text"] = JsonSerializer.SerializeToElement("Previous recipient fixture") }, [], now));
            var directBeforeRecipientCheck = f.DirectCalls;
            var relayBeforeRecipientCheck = f.RelayCalls;
            using (var response = await SendReport(oldRecipientId))
                Expect(response.StatusCode == HttpStatusCode.Forbidden && f.DirectCalls == directBeforeRecipientCheck
                    && f.RelayCalls == relayBeforeRecipientCheck && outbox.List().Length == 0,
                    "A retained envelope for a previous recipient cannot bypass the configured private chat.");
            now = now.AddMinutes(2); f.DirectAvailable = true; f.DirectError = 400;
            var relayBeforeBadRequest = f.RelayCalls;
            using (var response = await SendReport(Guid.NewGuid().ToString("N")))
                Expect(response.StatusCode == HttpStatusCode.BadRequest && f.RelayCalls == relayBeforeBadRequest && outbox.List().Length == 0,
                    "Permanent Telegram errors do not trigger fallback or endless retries.");
            f.DirectError = 429; f.Queued = true;
            using (var response = await SendReport(Guid.NewGuid().ToString("N")))
                Expect(response.StatusCode == HttpStatusCode.Accepted && f.NotBefore >= now.AddMinutes(4).ToUnixTimeMilliseconds(),
                    "Telegram retry_after is transferred to Netlify without an immediate second send.");
            var cancelDirectory = Path.Combine(directory, "cancel");
            var cancelOutbox = new RelayOutbox(cancelDirectory, b => b, b => b);
            var cancelWire = new FallbackWire(token) { WaitForCancellation = true };
            using var cancelTransport = new RelayTransport(settings, cancelOutbox, NullLogger<RelayTransport>.Instance,
                new HttpClient(cancelWire, false), new HttpClient(cancelWire, false));
            using var cancelClient = new HttpClient(new RelayHttpHandler(cancelTransport));
            using var cancelCts = new CancellationTokenSource();
            using var cancelRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/botfixture/sendMessage")
                { Content = new StringContent("{\"chat_id\":\"123\",\"text\":\"Cancellation fixture\"}", Encoding.UTF8, "application/json") };
            var cancelled = cancelClient.SendAsync(cancelRequest, cancelCts.Token);
            await cancelWire.DirectEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelCts.Cancel();
            try { using var response = await cancelled; Expect(false, "Cancellation must propagate."); }
            catch (OperationCanceledException) { Expect(cancelWire.RelayCalls == 0 && cancelOutbox.List().Length == 1,
                "Caller cancellation retains the local request without starting a fallback send."); }
            if (OperatingSystem.IsWindows())
            {
                var protectedDirectory = Path.Combine(directory, "dpapi");
                var protectedOutbox = new RelayOutbox(protectedDirectory);
                var entry = new RelayEnvelope(Guid.NewGuid().ToString("N"), "sendMessage",
                    new() { ["text"] = JsonSerializer.SerializeToElement("DPAPI private fixture") }, [], now);
                protectedOutbox.Save(entry);
                Expect(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(protectedDirectory, entry.Id + ".relay"))).Contains("DPAPI private fixture")
                    && new RelayOutbox(protectedDirectory).List()[0].Id == entry.Id, "Real Windows DPAPI protects and restores pending deliveries.");
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid fixture cleanup path.");
            Directory.Delete(resolved, true);
        }
    }

    private sealed class FallbackWire(string token) : HttpMessageHandler
    {
        public bool DirectAvailable = true, RelayAvailable = true, Queued, AllOriginsAllowed = true, MultiChunkCompleted;
        public int DirectCalls, RelayCalls, UploadCount, LargestUpload;
        public int? FailedPart;
        public int DirectError;
        public bool LoseRelayResponse, WaitForCancellation;
        public TaskCompletionSource DirectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long NotBefore;
        public string? DirectUploadType, LastAuthorization;
        public byte[]? DirectUpload;
        public JsonDocument? LastEnvelope;
        private readonly HashSet<string> _stored = [];
        private readonly Dictionary<string, Dictionary<int, byte[]>> _chunks = [];
        private static string Result => "{\"ok\":true,\"result\":{\"message_id\":42,\"date\":1793980800,\"chat\":{\"id\":123,\"type\":\"private\"}}}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            AllOriginsAllowed &= request.RequestUri!.Host is "api.telegram.org" or "relay.fixture.test";
            if (request.RequestUri!.Host == "api.telegram.org")
            {
                DirectCalls++;
                DirectEntered.TrySetResult();
                if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (!DirectAvailable) throw new HttpRequestException("Fixture direct link unavailable");
                if (DirectError > 0) return new((HttpStatusCode)DirectError)
                    { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = DirectError, parameters = new { retry_after = 300 } }), Encoding.UTF8, "application/json") };
                if (request.Content is MultipartFormDataContent multipart)
                    foreach (var part in multipart)
                        if (part.Headers.ContentDisposition?.FileName != null)
                        { DirectUploadType = part.Headers.ContentType?.MediaType; DirectUpload = await part.ReadAsByteArrayAsync(ct); }
                if (request.RequestUri.AbsolutePath.EndsWith("/getUpdates"))
                {
                    var body = JsonSerializer.Serialize(new { ok = true, result = new[] {
                        new { update_id = 10, message = new { date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = new { id = 123 }, chat = new { id = 123, type = "private" }, text = "/status" } },
                        new { update_id = 11, message = new { date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = new { id = 456 }, chat = new { id = 123, type = "private" }, text = "/screenshot" } },
                        new { update_id = 12, message = new { date = 1L, from = new { id = 123 }, chat = new { id = 123, type = "private" }, text = "/screenshot" } } } });
                    return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(Result, Encoding.UTF8, "application/json") };
            }
            RelayCalls++;
            if (!RelayAvailable) throw new HttpRequestException("Fixture relay unavailable");
            LastAuthorization = request.Headers.Authorization?.ToString();
            Expect(LastAuthorization == "Bearer " + token, "Netlify receives the existing bot credential in its authorization header.");
            if (request.RequestUri.AbsolutePath == "/api/upload-image")
            {
                UploadCount++;
                string Header(string key) => request.Headers.GetValues(key).Single();
                var part = int.Parse(Header("X-Relay-Part"));
                var parts = int.Parse(Header("X-Relay-Parts"));
                if (part == FailedPart) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                var data = await request.Content!.ReadAsByteArrayAsync(ct);
                LargestUpload = Math.Max(LargestUpload, data.Length);
                var key = Header("X-Relay-Delivery") + "/" + Header("X-Relay-Field");
                if (!_chunks.TryGetValue(key, out var chunks)) _chunks[key] = chunks = [];
                chunks[part] = data;
                if (chunks.Count == parts)
                {
                    var assembled = Enumerable.Range(0, parts).SelectMany(index => chunks[index]).ToArray();
                    Expect(Encoding.ASCII.GetString(assembled, 8, 4) == "WEBP"
                        && assembled.Length == int.Parse(Header("X-Relay-Bytes"))
                        && Convert.ToHexString(SHA256.HashData(assembled)).ToLowerInvariant() == Header("X-Relay-Sha256"),
                        "Chunk assembly preserves WebP bytes, total size, and SHA256.");
                    MultiChunkCompleted |= parts > 1;
                }
                return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            LastEnvelope?.Dispose();
            LastEnvelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var id = LastEnvelope.RootElement.GetProperty("id").GetString()!;
            if (LastEnvelope.RootElement.GetProperty("method").GetString() == "getDelivery")
            {
                if (!_stored.Contains(id)) return new(HttpStatusCode.NotFound) { Content = new StringContent("{\"ok\":false,\"error_code\":404}") };
            }
            else
            {
                _stored.Add(id);
                if (LoseRelayResponse) throw new HttpRequestException("Fixture accepted by relay; response lost");
                if (LastEnvelope.RootElement.TryGetProperty("notBefore", out var next)) NotBefore = next.GetInt64();
            }
            var response = new HttpResponseMessage(Queued ? HttpStatusCode.Accepted : HttpStatusCode.OK)
                { Content = new StringContent(Queued ? "{\"ok\":false,\"error_code\":503,\"description\":\"Queued\"}" : Result, Encoding.UTF8, "application/json") };
            response.Headers.Add("X-Relay-Stored", "true");
            return response;
        }
    }
}
