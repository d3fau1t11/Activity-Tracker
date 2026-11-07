using System.IO;

namespace Activity_Tracker.Relay;

public static class RelayDeviceIdentity
{
    public static string GetOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "instance-id.txt");
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, Guid.NewGuid().ToString("N"));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var id = File.ReadAllText(path).Trim();
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("The saved device identity is invalid.");
        return id;
    }
}
