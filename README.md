# Activity Tracker CLI

A Windows-based activity monitoring application that tracks application usage and sends reports to Telegram. Built with C# .NET 10, it runs as a background service and provides remote control via Telegram bot commands.

## Features

- **Window Monitoring**: Tracks active windows and application usage in real-time
- **Session Tracking**: Records duration of each application/window session
- **Telegram Integration**: Sends activity reports to your Telegram bot
- **Multiple PCs with One Bot**: Automatic device identities, PC selection, and separate command queues
- **Remote Control**: Control tracking via Telegram commands
- **Multi-User Support**: Works for all Windows accounts on the system
- **Offline Storage**: Stores reports locally when offline, syncs when connection restored
- **Privacy Features**: Optional window title hashing and sensitive keyword filtering
- **System Utilities**: Screenshot, webcam, system info, user management via Telegram
- **System Tray**: Visible tracking/paused status, local start/pause controls, and exit
- **Telegram Analytics**: Daily totals, top applications, seven-day trends, and paginated session history inside the bot
- **Daily Digest**: A configurable Telegram summary of the previous calendar day
- **Opt-in Clipboard History**: The latest 15 copied text items and images, retrieved on request through Telegram

## Requirements

- Windows 10 or later
- .NET 10.0 Runtime (or use self-contained build)
- Telegram Bot Token (from @BotFather)
- Telegram Chat ID (from @userinfobot)

## Installation

### 1. Clone or Download

```bash
git clone <repository-url>
cd Activity Tracker CLI
```

### 2. Configure Telegram Credentials

For a fresh checkout, create the local settings file first (PowerShell):
```powershell
Copy-Item appsettings.example.json appsettings.json
```
Keep an existing `appsettings.json` when upgrading. New settings have defaults, so existing configurations continue to work.

**Option A: Use Setup Window**
```bash
dotnet run -- --setup
```
This opens a WPF window to enter your bot token and chat ID.

**Option B: Manual Configuration**
Create `telegram_config.json` in the project directory:
```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHAT_ID"
  }
}
```

### 3. Build and Run

```bash
dotnet run
```

### 4. Install Auto-Start (Optional)

To start automatically when you sign in:
```bash
dotnet run -- --install-persistence
```

This requires administrator privileges and installs:
- System-wide Startup folder shortcut
- System-wide Registry Run key entry

## Configuration

### Shared Telegram bot, multiple PCs, and Netlify fallback

Multiple PCs can use the same preset Telegram bot token and private user/chat ID. Keep those credentials in ignored local configuration and set the same two values on Netlify. Each PC automatically creates a stable installation ID when `DeviceId` is `auto`. Restarting, changing networks, or renaming the PC does not replace that ID. Copy the application and configuration to other PCs while keeping each PC's own identity folder in `%APPDATA%/ActivityTracker/relay/identity`.

Add this section to the `appsettings.json` beside the runnable Windows app:

```json
{
  "Relay": {
    "Enabled": true,
    "SharedBot": true,
    "BaseUrl": "https://your-site.netlify.app",
    "DeviceId": "auto",
    "PollSeconds": 60,
    "DirectTimeoutSeconds": 8,
    "RecoverySeconds": 60,
    "LocalRetentionDays": 7
  }
}
```

An optional `DeviceName` gives the PC a friendly label of up to 40 characters; the default is its computer name. Leave `DeviceId` as `auto` on each PC, or give every PC a distinct explicit ID. An ID already claimed by another installation is rejected. Registrations use the preset bot credential, so the token holder can register devices without a separate login.

Deploy the repository to one Netlify Free project with base directory `relay-server`, build command `npm run check`, publish directory `public`, and functions directory `netlify/functions`. Add `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` in Netlify's environment-variable UI using the default All scopes, then deploy/publish. There is no per-PC Netlify environment variable. Confirm four functions: `telegram`, `telegram-webhook`, `upload-image`, and `retry-deliveries`.

Keep all trackers stopped while configuring. Create the ignored local `relay-server/.env` with those two preset values plus `RELAY_SITE_URL`, then run `npm.cmd ci`, `npm.cmd run check:env`, and `npm.cmd run configure` from `relay-server`. The setup installs one authenticated Telegram webhook and drops old pending Telegram updates. Restart the rebuilt Windows apps afterward. The full local setup guide is `relay-server/SETUP.md`.

Send `/devices` to choose a PC; selection opens that PC's menu. `/device` shows the selected PC and `/select <device-ID-or-selector>` changes it. Text commands apply to that selection. Buttons in an existing PC menu stay tied to that PC even after selecting another one. The app registers during its first relay request, normally within a polling interval after startup. The registry supports up to 32 PCs and marks a PC offline after 90 seconds without a request.

Incoming commands always pass through Netlify's webhook and separate private command queues, preventing PCs from competing for Telegram updates. Each PC polls its queue every 30 seconds by default. Commands expire after two minutes and are checked again before execution; acknowledged commands survive restart without replay. A PC crash after executing a command but before acknowledgement can still repeat it. Repeated webhook delivery keeps its original target and command sequence.

Outgoing reports, replies, and requested images try Telegram directly first, then use Netlify on temporary failures. Replies carry a PC label when the message size permits it. Captured images become WebP; Telegram photo requests receive JPEG previews and requested image documents stay WebP. Encrypted outbox receipts preserve ordinary retry ownership across recovery. A timed-out send already accepted by Telegram can still duplicate. Cloud retries and cleanup run every five minutes.

Netlify Free functions and private Blobs handle device records, commands, and fallback deliveries without Firebase or a billing account. All shared PCs consume the same project's usage allowance, including regular command polls while direct Telegram is healthy. The sample uses a 60-second poll interval to reduce usage; command responses can take about a minute. Monitor the project's credits, including function compute, requests, images, and production deploys. If Netlify is unavailable or paused, incoming commands wait or expire; outgoing direct delivery can continue. Clipboard collection still requires local opt-in on each PC, and the Windows app must be running to execute commands.

Legacy single-PC relay operation remains available with `SharedBot: false` on Windows and `RELAY_MULTI_DEVICE=false` plus a matching `RELAY_DEVICE_ID` on Netlify; its setup removes the webhook. Rebuild and configure all PCs consistently when changing modes.

Edit `appsettings.json` to customize behavior:

```json
{
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN",
    "ChatId": "YOUR_CHAT_ID",
    "EnablePrivacyMode": false,
    "TimeoutSeconds": 30
  },
  "Worker": {
    "CheckIntervalSeconds": 2,
    "MinSessionDurationSeconds": 15,
    "IdleThresholdSeconds": 180,
    "IgnoreSystemProcesses": true,
    "IgnoreProcesses": ["svchost.exe", "dwm.exe"]
  },
  "DailyDigest": {
    "Enabled": true,
    "SendAt": "08:00",
    "SendEmptyDays": false
  },
  "Privacy": {
    "HashWindowTitles": false,
    "RemoveSensitiveKeywords": true,
    "SensitiveKeywords": ["password", "bank", "credit card"]
  }
}
```

Worker settings take effect after restarting the app. The poll interval is constrained to 1–300 seconds, the minimum session duration to 1–3,600 seconds, and the idle threshold to 10–86,400 seconds. Custom ignored process names accept either `name` or `name.exe`; these exclusions apply even when `IgnoreSystemProcesses` is false.

### Telegram analytics

Send `/analytics` or tap **Analytics** in the bot's main menu. The overview shows daily active time, application/session counts, and the five most-used apps. Switch between **Overview**, **Trends**, and **Sessions** using the inline buttons. **Trends** displays a seven-day bar chart and total; **Sessions** shows five sessions per page, newest first.

Use **Previous day**, **Next day**, **Today**, and the page buttons to browse, or specify a date directly:

```text
/analytics 2026-11-06
/trends 2026-11-06
/sessions 2026-11-06 2
```

Dates use the PC's local calendar and cannot be in the future. **Refresh** updates the current bot message. All views use retained reports from `%APPDATA%\ActivityTracker`, including pending and failed deliveries. Ongoing sessions appear once they finish. Sessions crossing midnight are apportioned to each day. Session titles follow the same privacy filtering, hiding, and hashing settings as outgoing activity reports; long names and titles are shortened to fit Telegram.

There is no desktop analytics window or web server. The previous `Dashboard:OpenOnStartup` setting is no longer used.

### Local monitoring controls

The Windows notification-area icon remains visible for monitoring status and local controls. A green play icon means tracking is active; a gray pause icon means tracking is paused or idle. Right-click for **Start tracking**, **Pause tracking**, or **Exit Activity Tracker**. Tracking starts paused. The tracker prevents duplicate hosts within the same Windows session and user.

### Clipboard history

Clipboard history is **off by default**. On the PC, right-click the Activity Tracker tray icon, choose **Enable clipboard history…**, and accept the explanation. This is a separate opt-in from activity tracking, with its own visible ON/OFF status in the tray. The tray icon is green while either activity tracking or clipboard collection is active. Telegram cannot enable clipboard collection remotely.

After opt-in, the app saves the **latest 15 copied items**, counting text and images together. A new item replaces the oldest when the history is full. Existing clipboard contents are not harvested on enable or app startup. Empty text and unsupported formats are ignored. Images copied as bitmap/PNG clipboard data are stored as PNG; copying an image file in Explorer is a file operation and is not collected as image content.

The app uses its **own history**, independent of Windows Win+V history and cloud clipboard sync. It reads new copy events without replacing the current clipboard or changing Windows clipboard settings. Source-app exclusion flags (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`, and `CanUploadToCloudClipboard`) are respected. Clipboard data can contain passwords and private messages; these flags are not a complete sensitive-content detector.

Send `/clipboard` or tap **Clipboard** in the Telegram main menu. The bot lists up to five items per page, showing only type, timestamp, and size. Select an item to retrieve its text or PNG image file. Nothing is sent automatically. `/clipboard 2` opens the second page; `/clipboard clear` removes saved items while leaving collection enabled. **Disable and clear clipboard history** in the local tray turns collection off and removes history.

A clipboard session lasts until **Windows sign-out or computer restart**. Restarting Activity Tracker within the same sign-in preserves both history and the opt-in. Locking the computer or sleeping does not reset history. The app identifies the Windows sign-in using its authentication ID and logon time. Sign-out/restart invalidates the old history and requires a new local opt-in. The session-end event clears files when possible; after an abrupt restart, old files are invalidated and removed when the app next starts.

Files are stored separately under `%APPDATA%\ActivityTracker\clipboard`, encrypted with Windows DPAPI for the signed-in user. Windows supplies encryption and current-clipboard access; it does not manage this history. Text is limited to 3,000 UTF-16 characters plus a truncation marker. Images are limited to 4 MiB as PNG and 16,777,216 pixels. Larger or unsupported items are skipped. Already-retrieved Telegram messages/files remain in the chat after local history expires.

### Daily Telegram digest

The digest is enabled by default and runs at **08:00 local PC time**, summarizing the previous calendar day. It includes active time, application/session counts, the five most-used apps, and a comparison with the previous day when available. It includes completed sessions and omits window titles and URLs. Individual activity reports continue to be sent as before.

- `DailyDigest:Enabled`: enable or disable scheduled summaries.
- `DailyDigest:SendAt`: local time in strict 24-hour `HH:mm` format.
- `DailyDigest:SendEmptyDays`: send a zero-activity summary; defaults to `false`.

These digest settings are reread each minute. The app must be running to send a digest. If it starts after the scheduled time, it processes the latest completed day; it does not send a backlog of missed days. Failed sends retry after 15 minutes. Successful or intentionally skipped dates are persisted in `daily_digest.json` to avoid repeats after normal restarts. As with other Telegram sends, an ambiguous network failure or a crash between delivery and recording success can cause a duplicate.

Activity report delivery uses a separate queue so slow requests do not hold up tracking. Its retry delays are 5 minutes, 15 minutes, 1 hour, 3 hours, 6 hours, then 24 hours, up to 10 attempts. The queue is checked every 30 seconds and when a new session completes.

Reports are created when a session ends: changing app/title, pausing, becoming idle, changing Windows session, crossing midnight, or shutting down the tracker. The default minimum is 15 seconds. Idle sessions end at the last input time. A long interruption in polling (over 30 seconds or three configured poll intervals, whichever is longer) closes the old session at its last observation rather than counting suspend time as activity. Unexpected polling errors are retried on the next cycle.

Completed sessions remain in memory if saving fails, and saving is retried while the app runs. Delivery confirmation is saved before archiving, so archive failures can be recovered after restart without resending. Legacy rotated pending files are merged back into the active queue and retained as `.recovered` backups. Corrupt report files are preserved with a `.corrupted` backup and cause an explicit error instead of silently being replaced with empty data. Unsaved memory-only reports cannot survive process termination while storage remains unavailable.

## Telegram Commands

### Control Commands
- `/track` - Start tracking
- `/stop` - Stop tracking
- `/status` - Show current tracking status
- `/whoami` - Show current Windows account being tracked
- `/persistence` - Manage auto-start settings

### Data Commands
- `/clipboard [page]` - List saved clipboard text and images (requires local opt-in)
- `/clipboard clear` - Clear the app's saved clipboard history
- `/analytics [YYYY-MM-DD]` - Daily totals and top applications
- `/trends [YYYY-MM-DD]` - Seven-day activity trend ending on the selected day
- `/sessions [YYYY-MM-DD] [page]` - Paginated session history
- `/stats` - View detailed statistics
- `/history` - View browser history
- `/pending` - Show pending reports
- `/clear` - Clear pending reports

### Utility Commands
- `/screenshot` - Capture screenshot
- `/webcam` - Capture webcam image
- `/sysinfo` - Show system information
- `/users` - List local user accounts
- `/processes` - Show running processes
- `/lock` - Lock workstation
- `/sleep` - Put computer to sleep

### Advanced Commands
- `/msg <text>` - Display message on screen
- `/open <url>` - Open URL in browser
- `/kill <process>` - Kill a process
- `/block <app>` - Block an application
- `/unblock <app>` - Unblock an application
- `/shutdown` - Stop Activity Tracker

## Multi-User Support

The application supports tracking for all Windows accounts on the system:

- **System-wide Persistence**: Uses Common Startup folder and HKLM registry
- **User Identification**: Activity reports include Windows username
- **Session Isolation**: Each user session is tracked independently
- **Automatic Startup**: Starts automatically for each user when they sign in

### Installing on Another PC

1. **Build standalone exe:**
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained
   ```

2. **Copy to target PC:**
   - Copy the `bin\Release\net10.0-windows\win-x64\publish\` folder
   - Transfer to the other PC

3. **Configure on target PC:**
   ```bash
   Activity Tracker.exe --setup
   Activity Tracker.exe --install-persistence
   ```

## Building for Production

### Self-Contained Build (includes .NET runtime)
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Output: `bin\Release\net10.0-windows\win-x64\publish\Activity Tracker.exe`

### Framework-Dependent Build (requires .NET runtime)
```bash
dotnet publish -c Release
```

## Command-Line Options

- `--setup` - Force Telegram setup window
- `--install-persistence` - Install auto-start (requires admin)
- `--uninstall-persistence` - Remove auto-start
- `--no-persist` - Skip auto-start prompt
- `--no-console` - Run without console window

## Troubleshooting

### Build Errors
- Ensure .NET 10.0 SDK is installed
- Run `dotnet restore` before building

### Telegram Not Receiving Reports
- Verify bot token and chat ID in configuration
- Check internet connection
- Use `/status` command to check pending reports
- Pending reports retry automatically when their backoff delay expires

### Window Monitoring Not Working
- Ensure app is running in user session (not as SYSTEM service)
- Reinstall persistence: `--uninstall-persistence` then `--install-persistence`
- Check Windows Event Viewer for errors

### Multi-User Issues
- Each user needs the app to start in their session
- System-wide persistence (Common Startup + HKLM) handles this automatically
- Use `/whoami` to verify which account is being tracked

## Security Notes

- **Sensitive Files**: `telegram_config.json` and `appsettings.json` are excluded from Git
- **Privacy**: Outgoing activity report and analytics session titles follow title hiding, hashing, and sensitive-keyword settings. Local reports retain original titles. Daily digests omit titles and URLs.
- **Authorization**: Commands require the configured positive private chat ID and the same sender ID. Group chats and foreign callback actors are rejected. The legacy `AllowMultipleUsers` bypass is ignored.
- **Credentials and logs**: The bot token authenticates the relay; user and device IDs restrict scope. Telegram payloads, upstream response bodies, and exception URLs are excluded from Telegram service logs.
- The local security review is `SECURITY_REVIEW.md`. This review removed the controller bypass and sensitive Telegram logs, patched SQLite, and tightened relay request limits and recipient checks. The bot token remains the secret credential; chat and device IDs restrict scope. Telegram bot chats are not end-to-end encrypted, and failover can duplicate a timed-out send.

## Project Structure

```
Activity Tracker CLI/
├── Program.cs                    # Entry point and DI configuration
├── Worker.cs                      # Window monitoring logic
├── TelegramBotService.cs          # Telegram API integration
├── TelegramCommandService.cs      # Bot command handling
├── LocalStorageService.cs         # Data persistence
├── StartupPersistence.cs          # Auto-start management
├── TrayService.cs                 # Desktop notification-area controls
├── Telegram/TelegramAnalytics.cs  # Bot analytics views and navigation
├── ActivitySummary.cs             # Shared daily and weekly calculations
├── ActivitySessionTracker.cs      # Testable session and idle-time accounting
├── ReportDeliveryService.cs       # Serialized delivery and archive recovery
├── ClipboardHistoryStore.cs       # Encrypted 15-item clipboard history
├── ClipboardMonitor.cs            # Opt-in text/image copy-event listener
├── WindowsLogonSession.cs         # Sign-in identity for clipboard session resets
├── DailyDigestService.cs          # Scheduled Telegram summaries
├── ActivityReport.cs              # Activity data model
├── appsettings.example.json       # Credential-free configuration template
├── appsettings.json               # Configuration (excluded from Git)
├── telegram_config.json           # Telegram credentials (excluded from Git)
└── README.md                      # This file
```

## Development checks

```powershell
dotnet build "Activity Tracker.csproj" --no-restore
dotnet run --project tests/ActivityTracker.Checks.csproj
python -B tests/support/security_audit.py
```

The check harness uses isolated temporary storage, fixture activity, and a fake HTTP handler. It exercises the production session tracker through storage and report delivery, including pause/resume, idle time, clock changes, midnight, interrupted polling, disk errors, offline retries, concurrent sends, cancellation, and archive recovery. It also covers digest scheduling, analytics navigation, message limits, title privacy, clipboard text/image retention, encryption boundaries, sign-in resets, and bot navigation. Shared-bot checks cover concurrent PC registration, stable identities, device-bound menu buttons, independent command queues, source-ID resets, acknowledgement recovery, and expiry. On Windows, it verifies that the OS sign-in identity can be read consistently. It does not read the live clipboard, start tracking, or contact Telegram.

## Contributing

Contributions are welcome! 
