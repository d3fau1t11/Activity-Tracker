namespace Activity_Tracker;

/// <summary>Session state driven by timestamped observations, independent of Windows and transport.</summary>
public sealed class ActivitySessionTracker(TimeSpan minimumDuration, TimeSpan idleThreshold, TimeSpan maximumObservationGap)
{
    private AppSession? _current;
    private DateTime? _lastObservation;
    private readonly Queue<ActivityReport> _unsaved = new();
    public bool Enabled { get; private set; }
    public bool IsIdle { get; private set; }
    public int UnsavedCount => _unsaved.Count;

    public void Start()
    {
        if (Enabled) return;
        Enabled = true;
        IsIdle = false;
        _lastObservation = null;
    }

    public void Stop(DateTime now)
    {
        End(_lastObservation is { } last && (now < last || now - last > maximumObservationGap) ? last : now);
        Enabled = false;
        IsIdle = false;
        _lastObservation = null;
    }

    public void Observe(DateTime now, string app, string title, TimeSpan idleTime, bool sessionActive = true)
    {
        if (!Enabled) return;
        // A suspend/resume, clock jump, or long missed polling interval is not active usage.
        if (_lastObservation is { } last)
        {
            if (now < last) { End(last); return; }
            if (now - last > maximumObservationGap)
            {
                var lastInput = idleTime >= idleThreshold ? now - idleTime : last;
                End(lastInput < last ? lastInput : last);
            }
        }
        _lastObservation = now;
        if (!sessionActive)
        {
            End(now);
            IsIdle = false;
            return;
        }
        IsIdle = idleTime >= idleThreshold;
        if (IsIdle)
        {
            End(now - idleTime);
            return;
        }
        if (string.IsNullOrEmpty(app)) { End(now); return; }
        if (_current != null && (_current.AppName != app || _current.WindowTitle != title)) End(now);
        if (_current != null && _current.StartTime.Date != now.Date)
        {
            End(now.Date);
            _current = new AppSession { AppName = app, WindowTitle = title, StartTime = now.Date, EndTime = now };
        }
        _current ??= new AppSession { AppName = app, WindowTitle = title, StartTime = now, EndTime = now };
        _current.EndTime = now;
    }

    private void End(DateTime at)
    {
        if (_current == null) return;
        _current.EndTime = SessionTiming.EndTime(_current.StartTime, at);
        if (_current.Duration >= minimumDuration) _unsaved.Enqueue(ActivityReport.FromSession(_current));
        _current = null;
    }

    public int SavePending(Action<ActivityReport> save)
    {
        var count = 0;
        while (_unsaved.TryPeek(out var report))
        {
            // Keep the same ID in memory until a durable save succeeds.
            save(report);
            _unsaved.Dequeue();
            count++;
        }
        return count;
    }
}
