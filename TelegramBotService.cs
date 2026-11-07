using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Activity_Tracker
{
    /// <summary>
    /// Service for sending activity reports to Telegram with robust error handling
    /// </summary>
    public class TelegramBotService
    {
        #region Fields

        private readonly ILogger<TelegramBotService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _botToken;
        private readonly string _chatId;
        private readonly bool _enablePrivacyMode;
        private readonly int _maxMessageLength;
        private readonly TimeSpan _timeout;

        // Privacy configurations
        private readonly bool _hashWindowTitles;
        private readonly bool _removeSensitiveKeywords;
        private readonly List<string> _sensitiveKeywords;
        private readonly string _sensitiveReplacementText;

        private const string TelegramApiBaseUrl = "https://api.telegram.org/bot";

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the TelegramBotService
        /// </summary>
        /// <param name="config">Configuration instance</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="httpClient">HttpClient instance (injected via HttpClientFactory)</param>
        public TelegramBotService(
            IConfiguration config,
            ILogger<TelegramBotService> logger,
            HttpClient httpClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            // Validate and load configuration
            ValidateConfiguration(config);

            _botToken = config["Telegram:BotToken"]!;
            _chatId = config["Telegram:ChatId"]!;

            // Load optional configuration with defaults
            _enablePrivacyMode = config.GetValue("Telegram:EnablePrivacyMode", false);
            _maxMessageLength = Math.Clamp(config.GetValue("Telegram:MaxMessageLength", 1000), 512, 4096);
            _timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Telegram:TimeoutSeconds", 30), 1, 300));

            // Load Privacy configurations
            _hashWindowTitles = config.GetValue("Privacy:HashWindowTitles", false);
            _removeSensitiveKeywords = config.GetValue("Privacy:RemoveSensitiveKeywords", true);
            _sensitiveKeywords = config.GetSection("Privacy:SensitiveKeywords").GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList()!;
            if (_sensitiveKeywords.Count == 0)
            {
                _sensitiveKeywords = new List<string> { "password", "bank", "credit card", "ssn", "social security", "login", "sign in" };
            }
            _sensitiveReplacementText = config.GetValue("Privacy:SensitiveReplacementText", "[Sensitive Content]")!;

            // Configure HttpClient
            _httpClient.Timeout = _timeout;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "ActivityTracker/1.0");

            _logger.LogInformation("🤖 Telegram Bot Service initialized");
            _logger.LogDebug("Chat ID: {ChatId}, Privacy Mode: {PrivacyMode}, Hash Window Titles: {HashTitles}, Remove Sensitive: {RemoveSensitive}",
                _chatId, _enablePrivacyMode, _hashWindowTitles, _removeSensitiveKeywords);
        }

        /// <summary>
        /// Validates that required configuration exists
        /// </summary>
        private void ValidateConfiguration(IConfiguration config)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(config["Telegram:BotToken"]))
                errors.Add("Telegram:BotToken is required. Get it from @BotFather on Telegram.");

            if (string.IsNullOrWhiteSpace(config["Telegram:ChatId"]))
                errors.Add("Telegram:ChatId is required. Get it by messaging @userinfobot on Telegram.");

            if (errors.Any())
            {
                var errorMessage = string.Join(Environment.NewLine, errors);
                _logger?.LogCritical("❌ Configuration validation failed: {Errors}", errorMessage);
                throw new InvalidOperationException($"Telegram configuration errors:{Environment.NewLine}{errorMessage}");
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sends an activity report to Telegram
        /// </summary>
        /// <param name="report">The activity report to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if sent successfully, false otherwise</returns>
        public async Task<bool> SendReportAsync(ActivityReport report, CancellationToken cancellationToken = default)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));

            try
            {
                _logger.LogDebug("📨 Attempting to send report {Id} ({App})",
                    report.Id, report.AppName);

                var message = FormatMessage(report);
                var success = await SendTelegramMessageAsync(message, isPreEscaped: true, cancellationToken, parseMode: "HTML", deliveryId: report.Id);

                if (success)
                {
                    _logger.LogDebug("✅ Report sent successfully: {App} ({Duration})",
                        report.AppName, DurationFormatter.Format(report.DurationSeconds));
                }
                else
                {
                    _logger.LogWarning("⚠️ Failed to send report: {App} (Attempt will be retried)",
                        report.AppName);
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⏹️ Sending cancelled for report {Id}", report.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ Error sending report {Id} ({ErrorType})", report.Id, ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// Sends an arbitrary system notification message to Telegram
        /// </summary>
        public async Task<bool> SendSystemNotificationAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return await SendTelegramMessageAsync(message, isPreEscaped: false, cancellationToken);
        }

        /// <summary>Digests use plain text so application names cannot inject formatting.</summary>
        public async Task<bool> SendDailyDigestAsync(string message, CancellationToken cancellationToken = default, DateOnly? digestDate = null)
        {
            try
            {
                var payload = new { chat_id = _chatId, text = message, disable_web_page_preview = true };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{TelegramApiBaseUrl}{_botToken}/sendMessage") { Content = content };
                if (digestDate.HasValue)
                {
                    var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"daily-digest:{_chatId}:{Environment.MachineName}:{Environment.UserName}:{digestDate:yyyy-MM-dd}"));
                    request.Headers.Add("X-Activity-Delivery-Id", new Guid(hash.AsSpan(0, 16)).ToString("N"));
                }
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var success = await IsSuccessfulResponseAsync(response, cancellationToken);
                if (!success)
                    _logger.LogWarning("Daily digest send failed with HTTP {Status}", response.StatusCode);
                return success;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Daily digest send failed ({ErrorType})", ex.GetType().Name);
                return false;
            }
        }

        #endregion

        /// <summary>Use the same title privacy rules for every outgoing activity view.</summary>
        public string FormatWindowTitle(string windowTitle)
        {
            if (string.IsNullOrEmpty(windowTitle)) return windowTitle;
            if (_enablePrivacyMode) return "[Window Title Hidden for Privacy]";
            if (_removeSensitiveKeywords && _sensitiveKeywords.Any(k =>
                windowTitle.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return _sensitiveReplacementText;
            return _hashWindowTitles && windowTitle != _sensitiveReplacementText ? HashString(windowTitle) : windowTitle;
        }

        #region Private Methods

        /// <summary>
        /// Formats the activity report into a Telegram message
        /// </summary>
        private string FormatMessage(ActivityReport report)
        {
            // Bound plain values before encoding, never truncate finished markup or entities.
            string Html(string? value, int limit)
            {
                value ??= string.Empty;
                if (value.Length > limit)
                {
                    if (char.IsHighSurrogate(value[limit - 1])) limit--;
                    value = value[..limit] + "...";
                }
                return WebUtility.HtmlEncode(value);
            }
            return $"""
                <b>Activity Report</b>

                <b>User:</b> {Html(report.UserName, 64)}
                <b>Application:</b> {Html(report.AppName, 96)}
                <b>Window Title:</b> {Html(FormatWindowTitle(report.WindowTitle), _maxMessageLength - 384)}
                <b>Duration:</b> {DurationFormatter.Format(report.DurationSeconds)}
                <b>Time:</b> {report.Timestamp:HH:mm}
                <b>Date:</b> {report.Timestamp:yyyy-MM-dd}
                """;
        }

        /// <summary>
        /// Sends a message to Telegram
        /// </summary>
        private async Task<bool> SendTelegramMessageAsync(string message, bool isPreEscaped = false, CancellationToken cancellationToken = default, string? parseMode = null, Guid? deliveryId = null)
        {
            try
            {
                var payload = new
                {
                    chat_id = _chatId,
                    text = isPreEscaped ? message : EscapeNonFormattingMarkdown(message),
                    parse_mode = parseMode ?? "MarkdownV2",
                    disable_notification = false,
                    disable_web_page_preview = true
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url = $"{TelegramApiBaseUrl}{_botToken}/sendMessage";


                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (deliveryId.HasValue) request.Headers.Add("X-Activity-Delivery-Id", deliveryId.Value.ToString("N"));
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (await IsSuccessfulResponseAsync(response, cancellationToken))
                {
                    _logger.LogTrace("✅ Telegram API responded with success");
                    return true;
                }

                _logger.LogWarning("Telegram API error: HTTP {StatusCode}", response.StatusCode);

                // Handle specific HTTP status codes
                switch (response.StatusCode)
                {
                    case System.Net.HttpStatusCode.Unauthorized:
                        _logger.LogError("❌ Invalid bot token. Please check Telegram:BotToken in configuration.");
                        break;
                    case System.Net.HttpStatusCode.BadRequest:
                        _logger.LogError("❌ Bad request to Telegram API. Check chat ID and message format.");
                        break;
                    case System.Net.HttpStatusCode.TooManyRequests:
                        _logger.LogWarning("⏳ Rate limited by Telegram. Will retry with backoff.");
                        break;
                    case System.Net.HttpStatusCode.NotFound:
                        _logger.LogError("❌ Chat not found. Please check Telegram:ChatId in configuration.");
                        break;
                }

                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError("Network error sending to Telegram ({ErrorType})", ex.GetType().Name);
                return false;
            }
            catch (JsonException ex)
            {
                _logger.LogError("📄 JSON serialization error ({ErrorType})", ex.GetType().Name);
                return false;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("⏹️ Telegram send cancelled");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError("⏱️ Telegram send timeout after {Timeout} ({ErrorType})", _timeout, ex.GetType().Name);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("❌ Unexpected error sending to Telegram ({ErrorType})", ex.GetType().Name);
                return false;
            }
        }

        #endregion

        private static async Task<bool> IsSuccessfulResponseAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (!response.IsSuccessStatusCode) return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return body.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }

        private static string HashString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes).ToLower().Substring(0, 16) + "...";
        }

        #region Helper Methods

        /// <summary>
        /// Escapes all MarkdownV2 special characters except bold and code tags
        /// </summary>
        private static string EscapeNonFormattingMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var result = new StringBuilder(text);
            
            // First, escape all existing backslashes
            result.Replace("\\", "\\\\");

            // Then, escape other MarkdownV2 special characters EXCEPT * and `
            var specialChars = new[] { '_', '[', ']', '(', ')', '~', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
            foreach (var c in specialChars)
            {
                result.Replace(c.ToString(), $"\\{c}");
            }

            return result.ToString();
        }

        #endregion
    }
}
