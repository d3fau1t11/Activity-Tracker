namespace Activity_Tracker;

public static class SessionTiming
{
    // Idle detection supplies the last-input time, which may predate this session.
    public static DateTime EndTime(DateTime startedAt, DateTime endedAt) =>
        endedAt < startedAt ? startedAt : endedAt;
}
