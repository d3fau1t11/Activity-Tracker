using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Activity_Tracker;

/// <summary>Observes new copy events only after local consent. Never reads Windows Win+V history.</summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private readonly ClipboardHistoryService _history;
    private readonly ILogger _logger;
    private readonly ListenerWindow _window;
    private readonly DispatcherTimer _retry;
    private uint _pendingSequence;
    private int _attempts;

    public ClipboardMonitor(ClipboardHistoryService history, ILogger logger)
    {
        _history = history;
        _logger = logger;
        _retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _retry.Tick += (_, _) => ReadPending();
        _pendingSequence = GetClipboardSequenceNumber();
        _window = new ListenerWindow(Changed);
        try { SystemEvents.SessionEnded += SessionEnded; }
        catch { _window.Dispose(); throw; }
        _history.SetListening(true);
    }

    public void IgnoreCurrentClipboard()
    {
        _retry.Stop();
        _pendingSequence = GetClipboardSequenceNumber();
    }

    private void Changed()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _pendingSequence) return;
        _pendingSequence = sequence;
        _attempts = 0;
        if (_history.Enabled) _retry.Start();
    }

    private void ReadPending()
    {
        _retry.Stop();
        if (!_history.Enabled || GetClipboardSequenceNumber() != _pendingSequence) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null || Excluded(data)) return;
            BitmapSource? image = null;
            if (data.GetDataPresent(DataFormats.Bitmap)) image = data.GetData(DataFormats.Bitmap) as BitmapSource;
            if (image == null && data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream
                && pngStream.Length <= ClipboardHistoryStore.MaximumImageBytes)
            {
                pngStream.Position = 0;
                image = BitmapDecoder.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            }
            if (image != null)
            {
                if ((long)image.PixelWidth * image.PixelHeight > 16_777_216) return;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                if (stream.Length <= ClipboardHistoryStore.MaximumImageBytes && GetClipboardSequenceNumber() == _pendingSequence)
                    _history.AddImage(stream.ToArray(), image.PixelWidth, image.PixelHeight);
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
                && GetClipboardSequenceNumber() == _pendingSequence)
                _history.AddText(text);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            // The copying application may briefly hold the clipboard lock.
            if (++_attempts < 5) _retry.Start();
            else _logger.LogDebug("Clipboard item was unavailable after retries.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save a clipboard item");
        }
    }

    private static bool Excluded(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return true;
        foreach (var name in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!data.GetDataPresent(name)) continue;
            var flag = data.GetData(name);
            if (flag is MemoryStream stream)
            {
                var bytes = stream.ToArray();
                if (bytes.Length < 4 || BitConverter.ToUInt32(bytes, 0) == 0) return true;
            }
            else if (flag is int number) { if (number == 0) return true; }
            else if (flag is uint unsigned) { if (unsigned == 0) return true; }
            else if (flag is bool enabled) { if (!enabled) return true; }
            else return true; // An unknown privacy flag is not permission to retain the item.
        }
        return false;
    }

    private void SessionEnded(object sender, SessionEndedEventArgs args) => _history.EndSession();

    public void Dispose()
    {
        _retry.Stop();
        _history.SetListening(false);
        SystemEvents.SessionEnded -= SessionEnded;
        _window.Dispose();
    }

    private sealed class ListenerWindow : Forms.NativeWindow, IDisposable
    {
        private readonly Action _changed;
        public ListenerWindow(Action changed)
        {
            _changed = changed;
            CreateHandle(new Forms.CreateParams { Caption = "Activity Tracker clipboard listener", Parent = new IntPtr(-3) });
            if (!AddClipboardFormatListener(Handle))
            {
                var error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x031D) _changed();
            base.WndProc(ref message);
        }
        public void Dispose() { if (Handle != IntPtr.Zero) { RemoveClipboardFormatListener(Handle); DestroyHandle(); } }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
