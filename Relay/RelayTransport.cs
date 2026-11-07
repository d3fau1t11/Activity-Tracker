using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Activity_Tracker.Relay;

/// <summary>Direct Telegram first; encrypted deliveries fall back to authenticated Netlify.</summary>
public sealed class RelayTransport : BackgroundService
{
    private readonly RelaySettings _settings;
    private readonly RelayOutbox _outbox;
    private readonly HttpClient _direct, _relay;
    private readonly ILogger<RelayTransport> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private long _retryDirectAt;
    private readonly SemaphoreSlim _deliveries = new(1, 1);
    private static readonly HashSet<string> Durable = new(StringComparer.OrdinalIgnoreCase)
        { "sendMessage", "sendPhoto", "sendDocument", "editMessageText" };
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
        { "sendMessage", "sendPhoto", "sendDocument", "editMessageText", "getMe", "getUpdates", "sendChatAction", "setMyCommands", "answerCallbackQuery" };

    public RelayTransport(RelaySettings settings, RelayOutbox outbox, ILogger<RelayTransport> logger,
        HttpClient? direct = null, HttpClient? relay = null, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings; _outbox = outbox; _logger = logger; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _direct = direct ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        _relay = relay ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.RequestUri!.AbsolutePath.Split('/').Last();
        if (!Allowed.Contains(method)) throw new InvalidDataException("Unsupported Telegram method.");
        var envelope = await PrepareAsync(request, method, ct);
        if (envelope.Fields.TryGetValue("chat_id", out var chat) && chat.ToString() != _settings.ChatId)
            throw new InvalidDataException("Only the configured private chat is authorized.");
        if (_settings.SharedBot && Durable.Contains(method)) RelayRouting.Decorate(_settings, envelope.Fields);
        var durable = Durable.Contains(method);
        if (durable) await _deliveries.WaitAsync(ct);
        try
        {
            if (durable) envelope = _outbox.Save(envelope);
            return await SendCoreAsync(envelope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Telegram fallback deferred ({Type}); saved deliveries remain encrypted locally.", ex.GetType().Name);
            return Unavailable();
        }
        finally { if (durable) _deliveries.Release(); }
    }

    public static async Task<RelayEnvelope> PrepareAsync(HttpRequestMessage request, string method, CancellationToken ct)
    {
        var fields = new Dictionary<string, JsonElement>();
        var files = new List<RelayFile>();
        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                var disposition = part.Headers.ContentDisposition;
                var field = disposition?.Name?.Trim('"') ?? throw new InvalidDataException("Missing multipart field.");
                var filename = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
                if (filename != null)
                {
                    var data = await part.ReadAsByteArrayAsync(ct);
                    if (data.Length > 32 * 1024 * 1024) throw new InvalidDataException("Source image exceeds 32 MiB.");
                    // Current uploads are screenshots, webcam snapshots, and locally opted-in clipboard PNGs.
                    var lossless = method.Equals("sendDocument", StringComparison.OrdinalIgnoreCase)
                        || filename.StartsWith("screenshot", StringComparison.OrdinalIgnoreCase);
                    var encoded = WebpImages.Convert(data, lossless);
                    files.Add(new(field, Path.GetFileNameWithoutExtension(filename) + ".webp", "image/webp", encoded));
                }
                else fields[field] = JsonSerializer.SerializeToElement(await part.ReadAsStringAsync(ct));
            }
        }
        else if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            if (request.Content.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
            {
                foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var halves = pair.Split('=', 2);
                    fields[WebUtility.UrlDecode(halves[0])] = JsonSerializer.SerializeToElement(WebUtility.UrlDecode(halves.ElementAtOrDefault(1) ?? ""));
                }
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                foreach (var property in json.RootElement.EnumerateObject()) fields[property.Name] = property.Value.Clone();
            }
        }
        // Telegram.Bot assigns attachment IDs distinct from the logical photo/document parameter.
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var logical = fields.FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String
                && p.Value.GetString() == "attach://" + file.Field).Key ?? file.Field;
            if (logical is not ("photo" or "document")) throw new InvalidDataException("Unsupported image attachment field.");
            fields[logical] = JsonSerializer.SerializeToElement("attach://" + logical);
            files[index] = file with { Field = logical };
        }
        var id = request.Headers.TryGetValues("X-Activity-Delivery-Id", out var ids) ? ids.First() : Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid delivery ID.");
        return new(id, method, fields, files, DateTimeOffset.UtcNow, request.Headers.Contains("X-Activity-Delivery-Id"));
    }


    private async Task<HttpResponseMessage> SendCoreAsync(RelayEnvelope envelope, CancellationToken ct)
    {
        if (_settings.SharedBot && envelope.Method == "answerCallbackQuery")
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json") };
        if (_settings.SharedBot && envelope.Method == "getUpdates")
        {
            // Only Netlify consumes Telegram updates. Each PC pulls its own command queue.
            await Task.Delay(TimeSpan.FromSeconds(_settings.PollSeconds), ct);
            return await SubmitRelayAsync(envelope, 0, ct);
        }
        var durable = Durable.Contains(envelope.Method);
        if (envelope.Fields.TryGetValue("chat_id", out var queuedChat) && queuedChat.ToString() != _settings.ChatId)
        {
            var rejected = new RelayReceipt(envelope.Id, false, 403,
                "{\"ok\":false,\"error_code\":403,\"description\":\"Queued recipient no longer matches the configured private chat\"}", _clock().AddDays(30));
            if (durable) { _outbox.SaveReceipt(rejected); _outbox.Remove(envelope.Id); }
            return Cached(rejected);
        }
        var receipt = durable ? _outbox.GetReceipt(envelope.Id, _clock()) : null;
        if (receipt?.Status > 0)
        {
            _outbox.Remove(envelope.Id);
            return Cached(receipt);
        }
        if (receipt?.Relayed != true && _clock().ToUnixTimeMilliseconds() >= Interlocked.Read(ref _retryDirectAt))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_settings.DirectTimeoutSeconds));
                using var direct = BuildDirect(envelope);
                var response = await _direct.SendAsync(direct, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                JsonNode? body;
                try { body = JsonNode.Parse(text); }
                catch { response.Dispose(); throw new HttpRequestException("Telegram returned an invalid API response."); }
                if (body?["ok"] == null) { response.Dispose(); throw new HttpRequestException("Telegram returned an invalid API response."); }
                var status = (int)response.StatusCode;
                var errorCode = body["error_code"]?.GetValue<int>() ?? status;
                if (status < 500 && status != 429 && errorCode < 500 && errorCode != 429)
                {
                    Interlocked.Exchange(ref _retryDirectAt, 0);
                    if (envelope.Method == "getUpdates" && body["result"] is JsonArray updates)
                    {
                        for (var index = 0; index < updates.Count; index++)
                            if (!AuthorizedUpdate(updates[index]))
                                updates[index] = new JsonObject { ["update_id"] = updates[index]?["update_id"]?.DeepClone() };
                        response.Content.Dispose();
                        response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    }
                    if (durable)
                    {
                        _outbox.SaveReceipt(new(envelope.Id, false, status, text, _clock().AddDays(30)));
                        _outbox.Remove(envelope.Id);
                    }
                    return response;
                }
                var retryAfter = body["parameters"]?["retry_after"]?.GetValue<int>() ?? 0;
                response.Dispose();
                if (durable && retryAfter > 0)
                    receipt = new(envelope.Id, true, 0, "", _clock().AddDays(30), _clock().AddSeconds(Math.Clamp(retryAfter, 0, 86400)).ToUnixTimeMilliseconds());
                OpenFallback();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { OpenFallback(); }
            catch (OperationCanceledException) { OpenFallback(); }
        }
        if (durable)
        {
            // Persist route ownership before any relay POST. Recovery must not send a cloud-owned job directly again.
            receipt ??= new(envelope.Id, true, 0, "", _clock().AddDays(30));
            _outbox.SaveReceipt(receipt);
        }
        if (envelope.Method == "getUpdates")
            await Task.Delay(TimeSpan.FromSeconds(_settings.PollSeconds), ct);
        var relayed = await SubmitRelayAsync(envelope, receipt?.NotBefore ?? 0, ct);
        if (durable && relayed.Headers.Contains("X-Relay-Stored"))
        {
            if (relayed.StatusCode != HttpStatusCode.Accepted)
                _outbox.SaveReceipt(new(envelope.Id, true, (int)relayed.StatusCode,
                    await relayed.Content.ReadAsStringAsync(ct), _clock().AddDays(30)));
            _outbox.Remove(envelope.Id);
        }
        return relayed;
    }

    private bool AuthorizedUpdate(JsonNode? update)
    {
        var callback = update?["callback_query"];
        var message = update?["message"];
        var chat = callback?["message"]?["chat"] ?? message?["chat"];
        var actor = callback?["from"] ?? message?["from"];
        if (chat?["type"]?.ToString() != "private" || chat?["id"]?.ToString() != _settings.ChatId
            || actor?["id"]?.ToString() != _settings.ChatId) return false;
        var sentAt = message?["date"]?.GetValue<long>();
        return sentAt == null || sentAt * 1000 >= _clock().ToUnixTimeMilliseconds() - 120_000;
    }

    private HttpRequestMessage BuildDirect(RelayEnvelope envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{_settings.BotToken}/{envelope.Method}");
        if (envelope.Files.Count == 0)
        {
            var fields = new Dictionary<string, JsonElement>(envelope.Fields);
            if (envelope.Method == "getUpdates") fields["timeout"] = JsonSerializer.SerializeToElement(5);
            request.Content = JsonContent.Create(fields);
        }
        else
        {
            var multipart = new MultipartFormDataContent();
            foreach (var field in envelope.Fields)
                if (!envelope.Files.Any(file => file.Field == field.Key))
                    multipart.Add(new StringContent(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText()), field.Key);
            foreach (var file in envelope.Files)
            {
                var photo = envelope.Method == "sendPhoto";
                var part = new ByteArrayContent(photo ? WebpImages.PhotoPreview(file.Data) : file.Data);
                part.Headers.ContentType = new MediaTypeHeaderValue(photo ? "image/jpeg" : "image/webp");
                multipart.Add(part, file.Field, photo ? Path.ChangeExtension(file.Name, ".jpg") : file.Name);
            }
            request.Content = multipart;
        }
        return request;
    }

    private void OpenFallback()
    {
        var until = _clock().AddSeconds(_settings.RecoverySeconds).ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _retryDirectAt, until);
    }

    private HttpRequestMessage RelayRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _settings.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BotToken);
        return request;
    }

    private async Task<HttpResponseMessage> SubmitRelayAsync(RelayEnvelope envelope, long notBefore, CancellationToken ct)
    {
        if (Durable.Contains(envelope.Method))
        {
            using var lookup = RelayRequest("/api/telegram");
            lookup.Content = JsonContent.Create(new { deviceId = _settings.DeviceId, deviceName = _settings.DeviceName,
                instanceId = _settings.InstanceId, id = envelope.Id,
                method = "getDelivery", fields = new { }, files = Array.Empty<object>() });
            var previous = await _relay.SendAsync(lookup, ct);
            if (previous.StatusCode != HttpStatusCode.NotFound || previous.Headers.Contains("X-Relay-Stored")) return previous;
            previous.Dispose();
        }
        var files = new List<object>();
        foreach (var file in envelope.Files)
        {
            const int chunkBytes = 1024 * 1024;
            var parts = (file.Data.Length + chunkBytes - 1) / chunkBytes;
            var hash = Convert.ToHexString(SHA256.HashData(file.Data)).ToLowerInvariant();
            for (var part = 0; part < parts; part++)
            {
                using var upload = RelayRequest("/api/upload-image");
                upload.Headers.Add("X-Relay-Device", _settings.DeviceId);
                upload.Headers.Add("X-Relay-Instance", _settings.InstanceId);
                upload.Headers.Add("X-Relay-Name", Uri.EscapeDataString(_settings.DeviceName));
                upload.Headers.Add("X-Relay-Delivery", envelope.Id);
                upload.Headers.Add("X-Relay-Field", file.Field);
                upload.Headers.Add("X-Relay-Part", part.ToString());
                upload.Headers.Add("X-Relay-Parts", parts.ToString());
                upload.Headers.Add("X-Relay-Bytes", file.Data.Length.ToString());
                upload.Headers.Add("X-Relay-Sha256", hash);
                var offset = part * chunkBytes;
                upload.Content = new ByteArrayContent(file.Data, offset, Math.Min(chunkBytes, file.Data.Length - offset));
                upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var uploaded = await _relay.SendAsync(upload, ct);
                if (!uploaded.IsSuccessStatusCode) throw new HttpRequestException("Private Netlify image upload failed.");
            }
            files.Add(new { field = file.Field, name = file.Name, contentType = file.ContentType,
                path = $"devices/{_settings.DeviceId}/uploads/{envelope.Id}/{file.Field}.webp" });
        }
        using var message = RelayRequest("/api/telegram");
        message.Content = JsonContent.Create(new { deviceId = _settings.DeviceId, deviceName = _settings.DeviceName,
            instanceId = _settings.InstanceId, id = envelope.Id,
            method = envelope.Method, fields = envelope.Fields, files, notBefore });
        return await _relay.SendAsync(message, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _outbox.PruneReceipts(_clock());
                foreach (var entry in _outbox.List().Take(20))
                {
                    if (entry.CreatedAt.AddDays(_settings.RetentionDays) < _clock()) { _outbox.Remove(entry.Id); continue; }
                    await _deliveries.WaitAsync(stoppingToken);
                    try { using var response = await SendCoreAsync(entry, stoppingToken); }
                    finally { _deliveries.Release(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning("Saved delivery retry deferred ({Type}).", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
    private static HttpResponseMessage Cached(RelayReceipt receipt) => new((HttpStatusCode)receipt.Status)
        { Content = new StringContent(receipt.Body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Unavailable() => new(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{\"ok\":false,\"error_code\":503,\"description\":\"Telegram and relay unavailable; saved deliveries will retry\"}", Encoding.UTF8, "application/json") };
    public override void Dispose() { _direct.Dispose(); _relay.Dispose(); _deliveries.Dispose(); base.Dispose(); }
}

public sealed class RelayHttpHandler(RelayTransport transport) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.Host != "api.telegram.org" || !request.RequestUri.AbsolutePath.StartsWith("/bot", StringComparison.Ordinal))
            throw new InvalidOperationException("Only Telegram Bot API requests are supported.");
        return transport.SendAsync(request, ct);
    }
}
