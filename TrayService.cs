using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace Activity_Tracker;

/// <summary>Owns a dedicated desktop UI thread; no Telegram operation runs on that thread.</summary>
public sealed class TrayService(Worker worker,
    IHostApplicationLifetime lifetime, ClipboardHistoryService clipboard, ILogger<TrayService> logger) : IHostedService
{
    private Dispatcher? _dispatcher;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Environment.UserInteractive) { _finished.TrySetResult(); return; }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                using var activeIcon = CreateIcon(true);
                using var pausedIcon = CreateIcon(false);
                using var menu = new Forms.ContextMenuStrip();
                ClipboardMonitor? monitor = null;
                if (clipboard.Initialize())
                {
                    try { monitor = new ClipboardMonitor(clipboard, logger); }
                    catch (Exception ex) { logger.LogWarning(ex, "Clipboard listener is unavailable"); }
                }
                using var clipboardMonitor = monitor;
                using var tray = new Forms.NotifyIcon
                {
                    Icon = pausedIcon, Text = "Activity Tracker · Paused", ContextMenuStrip = menu, Visible = true
                };
                string Status() => !worker.IsTrackingEnabled ? "Paused" : worker.IsIdle ? "Idle · Waiting for activity" : "Tracking";

                var status = menu.Items.Add("Paused");
                status.Enabled = false;
                menu.Items.Add(new Forms.ToolStripSeparator());
                var start = menu.Items.Add("Start tracking");
                var pause = menu.Items.Add("Pause tracking");
                async Task SetTrackingAsync(bool enabled)
                {
                    start.Enabled = pause.Enabled = false;
                    try { await Task.Run(() => { if (enabled) worker.StartTracking(); else worker.StopTracking(); }); }
                    catch (Exception ex) { logger.LogError(ex, "Could not change tracking state from tray"); }
                }
                start.Click += async (_, _) => await SetTrackingAsync(true);
                pause.Click += async (_, _) => await SetTrackingAsync(false);
                menu.Items.Add(new Forms.ToolStripSeparator());
                var clipboardStatus = menu.Items.Add("Clipboard history: off");
                clipboardStatus.Enabled = false;
                var enableClipboard = menu.Items.Add("Enable clipboard history…");
                enableClipboard.Click += (_, _) =>
                {
                    if (Forms.MessageBox.Show(
                        "Keep the latest 15 copied text items and images for this Windows sign-in?\n\n" +
                        "Clipboard contents may include passwords or private messages. Saved items can be retrieved by your authorized Telegram chat using /clipboard. " +
                        "Nothing is sent automatically. History clears on sign-out or Windows restart.",
                        "Enable clipboard history", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Information,
                        Forms.MessageBoxDefaultButton.Button2) != Forms.DialogResult.Yes) return;
                    try { monitor?.IgnoreCurrentClipboard(); clipboard.EnableLocally(); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not enable clipboard history"); Forms.MessageBox.Show("Clipboard history could not be enabled."); }
                };
                var disableClipboard = menu.Items.Add("Disable and clear clipboard history");
                disableClipboard.Click += (_, _) =>
                {
                    try { clipboard.DisableAndClear(); monitor?.IgnoreCurrentClipboard(); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not clear clipboard history"); Forms.MessageBox.Show("Collection is off for this run, but saved files could not be cleared. Check the application logs."); }
                };
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("Exit Activity Tracker", null, (_, _) => lifetime.StopApplication());
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                void UpdateStatus()
                {
                    status.Text = Status();
                    tray.Text = $"Activity Tracker · {Status()} · Clipboard {(clipboard.Enabled ? "ON" : "off")}";
                    tray.Icon = (worker.IsTrackingEnabled && !worker.IsIdle) || clipboard.Enabled ? activeIcon : pausedIcon;
                    clipboardStatus.Text = !clipboard.Listening ? "Clipboard history: unavailable" : clipboard.Enabled ? "Clipboard history: ON (latest 15)" : "Clipboard history: off";
                    enableClipboard.Enabled = clipboard.Listening && !clipboard.Enabled;
                    disableClipboard.Enabled = clipboard.Available;
                    start.Enabled = !worker.IsTrackingEnabled;
                    pause.Enabled = worker.IsTrackingEnabled;
                }
                timer.Tick += (_, _) => UpdateStatus();
                UpdateStatus();
                timer.Start();
                ready.TrySetResult();
                Dispatcher.Run();
                timer.Stop();
                tray.Visible = false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Desktop interface stopped unexpectedly");
                ready.TrySetException(ex);
            }
            finally { _finished.TrySetResult(); }
        }) { IsBackground = true, Name = "Activity Tracker desktop" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await ready.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
        await _finished.Task.WaitAsync(cancellationToken);
    }

    private static Icon CreateIcon(bool tracking)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(tracking ? Color.FromArgb(65, 190, 124) : Color.FromArgb(109, 124, 131));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        if (tracking)
            graphics.FillPolygon(Brushes.White, [new Point(13, 9), new Point(24, 16), new Point(13, 23)]);
        else
        {
            graphics.FillRectangle(Brushes.White, 10, 10, 4, 12);
            graphics.FillRectangle(Brushes.White, 18, 10, 4, 12);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
