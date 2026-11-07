using Microsoft.Extensions.Logging;

namespace Activity_Tracker;

/// <summary>A single sender with persisted receipts and retryable archive operations.</summary>
public sealed class ReportDeliveryService(LocalStorageService storage, TelegramBotService telegram,
    ILogger<ReportDeliveryService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ActivityReport> _receipts = new();

    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var receipt in _receipts.Values.ToArray()) PersistReceipt(receipt);
            storage.ReconcileCompletedReports();
            var pending = storage.GetPendingReports(10, readyOnly: true);
            var sent = 0;
            for (var index = 0; index < pending.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index > 0) await Task.Delay(500, cancellationToken);
                var report = pending[index];
                bool success;
                try { success = await telegram.SendReportAsync(report, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not deliver report {Id}", report.Id);
                    success = false;
                }
                report.RecordSendAttempt(success, success ? null : "Telegram delivery failed");
                if (success)
                {
                    _receipts[report.Id] = report;
                    PersistReceipt(report);
                    sent++;
                }
                else
                {
                    storage.Save(report);
                    if (report.SendAttempts >= 10) storage.MoveToFailed(report);
                }
            }
            return sent;
        }
        finally { _gate.Release(); }
    }

    private void PersistReceipt(ActivityReport report)
    {
        // If archiving fails, the sent flag prevents network re-delivery after restart.
        storage.Save(report);
        storage.ArchiveReport(report);
        _receipts.Remove(report.Id);
    }
}
