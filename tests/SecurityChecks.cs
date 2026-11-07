using Activity_Tracker;
using Activity_Tracker.Telegram;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using Telegram.Bot.Types.Enums;

internal static partial class Program
{
    private static async Task CheckSecurityAsync()
    {
        Expect(TelegramAuthorization.IsAuthorized("123", 123, 123, ChatType.Private), "Owner private chat is authorized.");
        Expect(!TelegramAuthorization.IsAuthorized("123", 123, 456, ChatType.Private), "A foreign callback actor is rejected.");
        Expect(!TelegramAuthorization.IsAuthorized("123", 456, 456, ChatType.Private), "A foreign private chat is rejected.");
        Expect(!TelegramAuthorization.IsAuthorized("123", 123, 123, ChatType.Group), "Group commands are rejected.");
        Expect(!TelegramAuthorization.IsAuthorized("", 0, 0, ChatType.Private), "Missing owner configuration fails closed.");
        Expect(!TelegramAuthorization.IsOwnerChat("-123", "-123"), "A group ID cannot be configured as the owner.");
        var now = DateTime.UtcNow;
        Expect(TelegramAuthorization.IsFresh(now.AddSeconds(-90), now), "Recently queued commands can execute.");
        Expect(!TelegramAuthorization.IsFresh(now.AddSeconds(-121), now), "Commands that expire while waiting for execution are rejected.");
        Expect(!TelegramAuthorization.IsFresh(now.AddSeconds(31), now), "Invalid future command timestamps are rejected.");

        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "select sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        Expect(version >= new Version(3, 50, 2), "Loaded native SQLite fixes GHSA-2m69-gcr7-jv3q.");
        command.CommandText = "create table fixture(value text); insert into fixture values ('fixture'); select count(*) from fixture";
        Expect(Convert.ToInt64(command.ExecuteScalar()) == 1, "Patched SQLite supports normal database operations.");
        Console.WriteLine($"Native SQLite: {version}");

        const string marker = "private-fixture-payload";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "123:fixture-only-token", ["Telegram:ChatId"] = "123"
        }).Build();
        foreach (var networkError in new[] { false, true })
        {
            var logger = new SecurityLogger<TelegramBotService>();
            using var http = new HttpClient(new SecurityFailureHandler(networkError, marker));
            var service = new TelegramBotService(config, logger, http);
            Expect(!await service.SendReportAsync(Report(DateTime.Today, 60)), "Fixture failure is reported without live network.");
            Expect(!logger.Messages.Any(x => x.Contains(marker) || x.Contains("fixture-only-token")), "Telegram logs omit response bodies, exception URLs, and tokens.");
        }
    }

    private sealed class SecurityFailureHandler(bool networkError, string marker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (networkError) throw new HttpRequestException($"{request.RequestUri} {marker}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(marker) });
        }
    }

    private sealed class SecurityLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception?.ToString());
    }
}
