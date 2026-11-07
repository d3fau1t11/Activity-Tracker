using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Activity_Tracker.Relay;

public sealed record RelayFile(string Field, string Name, string ContentType, byte[] Data);
public sealed record RelayEnvelope(string Id, string Method, Dictionary<string, JsonElement> Fields,
    List<RelayFile> Files, DateTimeOffset CreatedAt, bool StableId = false);
public sealed record RelayReceipt(string Id, bool Relayed, int Status, string Body, DateTimeOffset ExpiresAt, long NotBefore = 0);

public sealed class RelayOutbox
{
    private readonly string _directory;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly object _gate = new();
    public RelayOutbox(string directory, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _directory = directory;
        var entropy = Encoding.UTF8.GetBytes("ActivityTracker.RelayOutbox.v1");
        _protect = protect ?? (b => OperatingSystem.IsWindows() ? ProtectedData.Protect(b, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Windows DPAPI is required for the relay outbox."));
        _unprotect = unprotect ?? (b => OperatingSystem.IsWindows() ? ProtectedData.Unprotect(b, entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Windows DPAPI is required for the relay outbox."));
        Directory.CreateDirectory(directory);
    }
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid delivery ID.");
        return Path.Combine(_directory, id + ".relay");
    }
    public RelayEnvelope Save(RelayEnvelope envelope)
    {
        lock (_gate)
        {
            var path = PathFor(envelope.Id);
            if (File.Exists(path)) return Read(path);
            if (Directory.EnumerateFiles(_directory, "*.relay").Count() >= 1000
                || Directory.EnumerateFiles(_directory, "*.relay").Sum(p => new FileInfo(p).Length) > 256L * 1024 * 1024)
                throw new IOException("Relay outbox is full. Restore connectivity or clear expired entries.");
            File.WriteAllBytes(path + ".tmp", _protect(JsonSerializer.SerializeToUtf8Bytes(envelope)));
            File.Move(path + ".tmp", path, true);
            return envelope;
        }
    }
    private RelayEnvelope Read(string path) => JsonSerializer.Deserialize<RelayEnvelope>(_unprotect(File.ReadAllBytes(path)))
        ?? throw new InvalidDataException("Invalid relay outbox entry.");
    public RelayEnvelope[] List()
    {
        lock (_gate) return Directory.EnumerateFiles(_directory, "*.relay").Select(Read).OrderBy(e => e.CreatedAt).ToArray();
    }
    public void Remove(string id) { lock (_gate) File.Delete(PathFor(id)); }
    public RelayReceipt? GetReceipt(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var path = Path.ChangeExtension(PathFor(id), ".receipt");
            if (!File.Exists(path)) return null;
            var receipt = JsonSerializer.Deserialize<RelayReceipt>(_unprotect(File.ReadAllBytes(path)))
                ?? throw new InvalidDataException("Invalid delivery receipt.");
            if (receipt.ExpiresAt > now) return receipt;
            File.Delete(path); return null;
        }
    }
    public void SaveReceipt(RelayReceipt receipt)
    {
        lock (_gate)
        {
            var path = Path.ChangeExtension(PathFor(receipt.Id), ".receipt");
            File.WriteAllBytes(path + ".tmp", _protect(JsonSerializer.SerializeToUtf8Bytes(receipt)));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void PruneReceipts(DateTimeOffset now)
    {
        lock (_gate)
            foreach (var path in Directory.EnumerateFiles(_directory, "*.receipt"))
                GetReceipt(Path.GetFileNameWithoutExtension(path), now);
    }
}
