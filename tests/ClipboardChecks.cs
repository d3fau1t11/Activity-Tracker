using Activity_Tracker;
using Activity_Tracker.Telegram;
using System.Security.Cryptography;
using System.Text;

internal static partial class Program
{
    private static void CheckClipboardHistory()
    {
        using var directory = new FixtureDirectory();
        using var aes = new AesGcm(RandomNumberGenerator.GetBytes(32), 16);
        byte[] Protect(byte[] plaintext)
        {
            var output = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28), output.AsSpan(12, 16));
            return output;
        }
        byte[] Unprotect(byte[] ciphertext)
        {
            if (ciphertext.Length < 28) throw new CryptographicException();
            var output = new byte[ciphertext.Length - 28];
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), output);
            return output;
        }
        ClipboardHistoryStore Open(string session = "fixture-signin-1") => new(directory.Path, session, Protect, Unprotect);
        var time = new DateTime(2026, 11, 6, 12, 0, 0);
        var store = Open();
        Expect(!store.Enabled && !store.AddText("not opted in", time) && store.List().Length == 0,
            "Clipboard history starts disabled and cannot store text before opt-in.");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aCfoAAAAASUVORK5CYII=");
        Expect(!store.AddImage(png, 1, 1, time), "Images are also rejected before clipboard opt-in.");
        store.Enable();
        store.AddText("private fixture text <&> 123", time);
        store.AddImage(png, 1, 1, time.AddSeconds(1));
        var entries = store.List();
        Expect(entries.Length == 2 && entries[0].Kind == ClipboardItemKind.Image && entries[1].Kind == ClipboardItemKind.Text,
            "Text and images share one newest-first history.");
        Expect(store.Read(entries[0].Id)!.Png!.SequenceEqual(png), "PNG bytes round-trip without changing the image.");
        Expect(store.Read(entries[1].Id)!.Text == "private fixture text <&> 123", "Text round-trips without interpreting markup.");
        foreach (var path in Directory.GetFiles(directory.Path))
            Expect(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("private fixture text"), "Clipboard contents are encrypted on disk.");
        var restarted = Open();
        Expect(restarted.Enabled && restarted.List().Length == 2 && restarted.Read(entries[0].Id)!.Png!.SequenceEqual(png),
            "App restart within the same Windows sign-in preserves consent and history.");
        for (var i = 0; i < 16; i++) restarted.AddText($"new copy {i}", time.AddMinutes(i));
        Expect(restarted.List().Length == 15 && restarted.Read(restarted.List()[0].Id)!.Text == "new copy 15",
            "A rolling history keeps the latest 15 items.");
        Expect(restarted.Read(entries[0].Id) == null && Directory.GetFiles(directory.Path, "*.clip").Length == 15,
            "Eviction removes older payloads and makes their existing bot buttons expire.");
        Expect(!restarted.AddImage(new byte[] { 1, 2, 3 }, 1, 1, time), "Non-PNG payloads are rejected.");
        Expect(!restarted.AddImage(png, 10000, 10000, time), "Oversized image dimensions are rejected.");
        var oversized = new byte[ClipboardHistoryStore.MaximumImageBytes + 1];
        png.AsSpan(0, 8).CopyTo(oversized);
        Expect(!restarted.AddImage(oversized, 1, 1, time), "Oversized image files do not consume a history slot.");
        restarted.AddText(new string('a', 2999) + "😀" + new string('b', 100), time);
        var truncated = restarted.Read(restarted.List()[0].Id)!.Text!;
        Expect(truncated.Length <= 3001 && !char.IsHighSurrogate(truncated[^2]), "Long text is bounded without splitting a Unicode surrogate pair.");

        var view = TelegramClipboard.Build(restarted.List(), restarted.Enabled, 0);
        Expect(view.Html.Contains("15/15") && !view.Html.Contains("new copy"), "The bot list exposes metadata only until an item is selected.");
        var firstItem = view.Buttons.SelectMany(row => row).First(button => button.Callback.StartsWith("clip:item:"));
        Expect(TelegramClipboard.TryParseCallback(firstItem.Callback, out var request) && request.ItemId == restarted.List()[0].Id,
            "Item buttons use stable IDs, independent of later clipboard changes.");
        var next = view.Buttons.SelectMany(row => row).Single(button => button.Text == "Next ›");
        Expect(TelegramClipboard.TryParseCallback(next.Callback, out request) && request.Page == 1, "Clipboard pagination advances correctly.");
        Expect(TelegramClipboard.Build(restarted.List(), true, int.MaxValue).Html.Contains("Page 3 of 3"), "Invalidated page numbers clamp safely.");
        foreach (var button in view.Buttons.SelectMany(row => row))
            Expect(Encoding.UTF8.GetByteCount(button.Callback) <= 64, "Clipboard callbacks fit Telegram's byte limit.");
        Expect(TelegramClipboard.TryParseCommand("/clipboard@fixture_bot 2", out request) && request.Page == 1,
            "Clipboard command supports a bot suffix and human page numbers.");
        Expect(TelegramClipboard.TryParseCommand("/clipboard clear", out request) && request.Action == "clear", "Explicit clear command is supported.");
        foreach (var command in new[] { "/clipboard on", "/clipboard enable", "/clipboard 0", "/clipboard -1", "/clipboard 99999999999", "/clipboard 1 extra" })
            Expect(!TelegramClipboard.TryParseCommand(command, out _), "Invalid commands cannot enable clipboard collection remotely.");
        Expect(!TelegramClipboard.TryParseCallback("clip:enable:1", out _) && !TelegramClipboard.TryParseCallback("clip:item:../history.bin", out _),
            "Callbacks cannot enable collection or supply filesystem paths.");

        restarted.Clear();
        Expect(restarted.Enabled && restarted.List().Length == 0, "Clearing history keeps the current opt-in active.");
        restarted.AddText("before disable", time);
        restarted.Clear(disable: true);
        Expect(!Open().Enabled && Open().List().Length == 0, "Disabling history clears data and persists the opt-out.");
        restarted.Enable();
        restarted.AddText("previous Windows sign-in", time);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "unrelated.txt"), "keep");
        var nextSession = Open("fixture-signin-2-after-reboot-or-signout");
        Expect(!nextSession.Enabled && nextSession.List().Length == 0 && Directory.GetFiles(directory.Path, "*.clip").Length == 0,
            "A new Windows sign-in clears both history and consent.");
        Expect(File.Exists(System.IO.Path.Combine(directory.Path, "unrelated.txt")), "Cleanup only removes files owned by the clipboard store.");

        nextSession.Enable();
        nextSession.AddText("committed item", time);
        var blocker = System.IO.Path.Combine(directory.Path, "history.bin.tmp");
        Directory.CreateDirectory(blocker);
        try { nextSession.AddText("uncommitted item", time); Expect(false, "Index write failure must be reported."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Expect(nextSession.List().Length == 1, "Failed writes do not publish uncommitted clipboard entries.");
        Directory.Delete(blocker);
        nextSession = Open("fixture-signin-2-after-reboot-or-signout");
        Expect(nextSession.List().Length == 1 && Directory.GetFiles(directory.Path, "*.clip").Length == 1,
            "Restart removes orphaned encrypted files without losing committed history.");
        File.WriteAllBytes(System.IO.Path.Combine(directory.Path, "history.bin"), new byte[] { 0, 1, 2 });
        nextSession = Open("fixture-signin-2-after-reboot-or-signout");
        Expect(!nextSession.Enabled && nextSession.List().Length == 0, "Unreadable clipboard indexes fail closed and require a new opt-in.");

        if (OperatingSystem.IsWindows())
        {
            var first = WindowsLogonSession.GetId();
            Expect(!string.IsNullOrWhiteSpace(first) && first == WindowsLogonSession.GetId(),
                "Windows returns a stable sign-in identity; no clipboard contents are accessed by this check.");
        }
    }
}
