using System.Security.Cryptography;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Activity_Tracker;

/// <summary>Clipboard state shared by the local opt-in controls and authenticated bot commands.</summary>
public sealed class ClipboardHistoryService(ILogger<ClipboardHistoryService> logger)
{
    private readonly object _gate = new();
    private ClipboardHistoryStore? _store;
    private bool _sessionEnded;
    private bool _locallyDisabled;
    private bool _listening;
    public string? Error { get; private set; }
    public bool Available { get { lock (_gate) return _store != null && !_sessionEnded; } }
    public bool Enabled { get { lock (_gate) return !_sessionEnded && !_locallyDisabled && _listening && _store?.Enabled == true; } }
    public bool Listening { get { lock (_gate) return _listening; } }
    public void SetListening(bool listening) { lock (_gate) _listening = listening; }

    public bool Initialize()
    {
        lock (_gate)
        {
            if (_sessionEnded) return false;
            if (_store != null) return true;
            try
            {
                var entropy = Encoding.UTF8.GetBytes("ActivityTracker.Clipboard.v1");
                _store = new ClipboardHistoryStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ActivityTracker", "clipboard"), WindowsLogonSession.GetId(),
                    bytes => ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser),
                    bytes => ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser));
                Error = null;
                return true;
            }
            catch (Exception ex)
            {
                Error = "Clipboard history is unavailable. Check the local tray and application logs.";
                logger.LogWarning(ex, "Could not initialize clipboard history");
                return false;
            }
        }
    }

    // Only the local tray calls this method. There is deliberately no remote-enable command.
    public void EnableLocally()
    {
        lock (_gate)
        {
            if (!Initialize() || !_listening) throw new InvalidOperationException("The clipboard listener is unavailable.");
            _store!.Enable();
            _locallyDisabled = false;
        }
    }
    public void DisableAndClear() { lock (_gate) { _locallyDisabled = true; _store?.Clear(disable: true); } }
    public void Clear() { lock (_gate) { if (!_sessionEnded) _store?.Clear(); } }
    public ClipboardItemInfo[] List() { lock (_gate) return _sessionEnded ? [] : _store?.List() ?? []; }
    public ClipboardItem? Read(Guid id) { lock (_gate) return _sessionEnded ? null : _store?.Read(id); }
    public bool AddText(string text) { lock (_gate) return Enabled && _store?.AddText(text, DateTime.Now) == true; }
    public bool AddImage(byte[] png, int width, int height) { lock (_gate) return Enabled && _store?.AddImage(png, width, height, DateTime.Now) == true; }

    public void EndSession()
    {
        lock (_gate)
        {
            _sessionEnded = true;
            try { _store?.Clear(disable: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not clear clipboard files at session end; the next sign-in will invalidate them."); }
        }
    }
}
