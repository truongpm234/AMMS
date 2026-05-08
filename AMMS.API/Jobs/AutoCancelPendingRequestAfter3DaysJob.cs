using AMMS.Application.Helpers;
using AMMS.Infrastructure.DBContext;
using AMMS.Shared.Helpers;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace AMMS.API.Jobs
{
    public sealed class AutoCancelPendingRequestAfter3DaysJob
    {
        private readonly AppDbContext _db;
        private readonly ILogger<AutoCancelPendingRequestAfter3DaysJob> _logger;

        public AutoCancelPendingRequestAfter3DaysJob(
            AppDbContext db,
            ILogger<AutoCancelPendingRequestAfter3DaysJob> logger)
        {
            _db = db;
            _logger = logger;
        }

        [DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
        public async Task RunAsync(CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();
            var cutoff = now.AddDays(-3);
            const string cancelReason = "Yêu cầu bị hủy do hệ thống quá tải";

            var candidates = await _db.order_requests
                .AsNoTracking()
                .Where(x =>
                    x.order_request_date != null &&
                    x.order_request_date <= cutoff &&
                    x.process_status == "Pending")
                .OrderBy(x => x.order_request_date)
                .Select(x => new
                {
                    x.order_request_id,
                    x.order_request_date,
                    x.customer_name,
                    x.customer_email
                })
                .ToListAsync(ct);

            if (candidates.Count == 0)
            {
                _logger.LogInformation(
                    "[AutoCancelPendingRequestAfter3DaysJob] No pending requests older than 3 days at {Now}",
                    now);
                return;
            }

            _logger.LogInformation(
                "[AutoCancelPendingRequestAfter3DaysJob] Found {Count} pending requests overdue 3 days",
                candidates.Count);

            var successCount = 0;
            var skipCount = 0;
            var failCount = 0;

            foreach (var item in candidates)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    var strategy = _db.Database.CreateExecutionStrategy();

                    var updated = await strategy.ExecuteAsync(async () =>
                    {
                        await using var tx = await _db.Database.BeginTransactionAsync(ct);

                        var req = await _db.order_requests
                            .FirstOrDefaultAsync(x => x.order_request_id == item.order_request_id, ct);

                        if (req == null)
                        {
                            await tx.RollbackAsync(ct);
                            return false;
                        }

                        if (!string.Equals(req.process_status, "Pending", StringComparison.OrdinalIgnoreCase))
                        {
                            await tx.RollbackAsync(ct);
                            return false;
                        }

                        if (!req.order_request_date.HasValue || req.order_request_date.Value > cutoff)
                        {
                            await tx.RollbackAsync(ct);
                            return false;
                        }

                        req.process_status = "Cancel";
                        req.reason = cancelReason;

                        await _db.SaveChangesAsync(ct);
                        await tx.CommitAsync(ct);
                        return true;
                    });

                    if (updated)
                    {
                        successCount++;
                        _logger.LogInformation(
                            "[AutoCancelPendingRequestAfter3DaysJob] Auto cancelled request successfully. RequestId={RequestId}, OrderRequestDate={OrderRequestDate}",
                            item.order_request_id,
                            item.order_request_date);
                    }
                    else
                    {
                        skipCount++;
                        _logger.LogInformation(
                            "[AutoCancelPendingRequestAfter3DaysJob] Skip request because state changed. RequestId={RequestId}",
                            item.order_request_id);
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    _logger.LogError(
                        ex,
                        "[AutoCancelPendingRequestAfter3DaysJob] Auto cancel failed. RequestId={RequestId}",
                        item.order_request_id);
                }
            }

            _logger.LogInformation(
                "[AutoCancelPendingRequestAfter3DaysJob] Done. Success={SuccessCount}, Skipped={SkipCount}, Failed={FailCount}",
                successCount,
                skipCount,
                failCount);
        }
    }
}