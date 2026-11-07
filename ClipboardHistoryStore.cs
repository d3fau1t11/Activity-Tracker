using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Activity_Tracker;

public enum ClipboardItemKind { Text, Image }
public sealed record ClipboardItemInfo(Guid Id, ClipboardItemKind Kind, DateTime CapturedAt, int Size, int Width = 0, int Height = 0);
public sealed record ClipboardItem(ClipboardItemInfo Info, string? Text, byte[]? Png);

/// <summary>App-owned, encrypted history for one Windows sign-in. No Windows clipboard-history APIs.</summary>
public sealed class ClipboardHistoryStore
{
    public const int Capacity = 15;
    public const int MaximumTextLength = 3000;
    public const int MaximumImageBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _session;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private HistoryIndex _index;
    private sealed record HistoryIndex(string Session, bool Enabled, List<ClipboardItemInfo> Items);

    public ClipboardHistoryStore(string directory, string session, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        if (string.IsNullOrWhiteSpace(session)) throw new ArgumentException("A sign-in session is required.", nameof(session));
        _directory = Path.GetFullPath(directory);
        _session = session;
        _protect = protect;
        _unprotect = unprotect;
        Directory.CreateDirectory(_directory);
        _index = new(session, false, []);
        if (File.Exists(IndexPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<HistoryIndex>(_unprotect(File.ReadAllBytes(IndexPath)));
                if (loaded == null || loaded.Session != session || loaded.Items == null || loaded.Items.Count > Capacity
                    || loaded.Items.Select(i => i.Id).Distinct().Count() != loaded.Items.Count)
                    throw new InvalidDataException("Clipboard history belongs to a different or invalid session.");
                _index = loaded;
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException or InvalidDataException)
            {
                WriteIndex(_index);
            }
        }
        else WriteIndex(_index);
        CleanupUnusedItems();
    }

    private string IndexPath => Path.Combine(_directory, "history.bin");
    private string ItemPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".clip");
    public bool Enabled { get { lock (_gate) return _index.Enabled; } }
    public ClipboardItemInfo[] List() { lock (_gate) return _index.Items.ToArray(); }

    public void Enable()
    {
        lock (_gate)
        {
            var next = _index with { Enabled = true };
            WriteIndex(next);
            _index = next;
        }
    }

    public void Clear(bool disable = false)
    {
        lock (_gate)
        {
            var next = new HistoryIndex(_session, !disable && _index.Enabled, []);
            WriteIndex(next);
            _index = next;
            CleanupUnusedItems();
        }
    }

    public bool AddText(string text, DateTime capturedAt)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > MaximumTextLength)
        {
            var length = char.IsHighSurrogate(text[MaximumTextLength - 1]) ? MaximumTextLength - 1 : MaximumTextLength;
            text = text[..length] + "…";
        }
        return Add(new(new(Guid.NewGuid(), ClipboardItemKind.Text, capturedAt, text.Length), text, null));
    }

    public bool AddImage(byte[] png, int width, int height, DateTime capturedAt)
    {
        if (png.Length == 0 || png.Length > MaximumImageBytes || width <= 0 || height <= 0
            || (long)width * height > 16_777_216) return false;
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        return Add(new(new(Guid.NewGuid(), ClipboardItemKind.Image, capturedAt, png.Length, width, height), null, png));
    }

    private bool Add(ClipboardItem item)
    {
        lock (_gate)
        {
            if (!_index.Enabled) return false;
            // One copy action can publish several formats; retain one item per observed clipboard version.
            var next = new HistoryIndex(_session, true, new[] { item.Info }.Concat(_index.Items).Take(Capacity).ToList());
            WriteEncrypted(ItemPath(item.Info.Id), JsonSerializer.SerializeToUtf8Bytes(item));
            WriteIndex(next);
            _index = next;
            CleanupUnusedItems();
            return true;
        }
    }

    public ClipboardItem? Read(Guid id)
    {
        lock (_gate)
        {
            if (!_index.Items.Any(i => i.Id == id)) return null;
            var item = JsonSerializer.Deserialize<ClipboardItem>(_unprotect(File.ReadAllBytes(ItemPath(id))));
            if (item == null || item.Info.Id != id) throw new InvalidDataException("Invalid clipboard entry.");
            return item;
        }
    }

    private void WriteIndex(HistoryIndex next) => WriteEncrypted(IndexPath, JsonSerializer.SerializeToUtf8Bytes(next));

    private void WriteEncrypted(string path, byte[] plaintext)
    {
        var encrypted = _protect(plaintext);
        File.WriteAllBytes(path + ".tmp", encrypted);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void CleanupUnusedItems()
    {
        var retained = _index.Items.Select(i => i.Id).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.clip"))
        {
            // Only files with our exact GUID naming convention belong to this store.
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) && !retained.Contains(id)) File.Delete(path);
        }
        foreach (var path in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            var name = Path.GetFileName(path);
            if (name == "history.bin.tmp" || (name.EndsWith(".clip.tmp", StringComparison.Ordinal)
                && Guid.TryParseExact(name[..^9], "N", out _))) File.Delete(path);
        }
    }
}
