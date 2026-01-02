using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class TaskScanService : ITaskScanService
    {
        private readonly AppDbContext _db;
        private readonly ITaskQrTokenService _tokenSvc;
        private readonly ITaskRepository _taskRepo;
        private readonly ITaskLogRepository _logRepo;
        private readonly IProductionRepository _prodRepo;
        private readonly IMachineRepository _machineRepo;

        public TaskScanService(
            AppDbContext db,
            ITaskQrTokenService tokenSvc,
            ITaskRepository taskRepo,
            ITaskLogRepository logRepo,
            IProductionRepository productionRepo,
            IMachineRepository machineRepo)
        {
            _db = db;
            _tokenSvc = tokenSvc;
            _taskRepo = taskRepo;
            _logRepo = logRepo;
            _prodRepo = productionRepo;
            _machineRepo = machineRepo;
        }

        public async Task<ScanTaskResult> ScanFinishAsync(ScanTaskRequest req)
        {
            if (!_tokenSvc.TryValidate(req.token, out var taskId, out var reason))
                throw new ArgumentException(reason);

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();

                try
                {
                    var t = await _taskRepo.GetByIdAsync(taskId)
                        ?? throw new Exception("Task not found");

                    if (!t.prod_id.HasValue || !t.seq_num.HasValue)
                        throw new Exception("Task missing prod_id/seq_num");

                    var prev = await _taskRepo.GetPrevTaskAsync(t.prod_id.Value, t.seq_num.Value);
                    if (prev != null && !string.Equals(prev.status, "Finished", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Previous step (task_id={prev.task_id}, seq={prev.seq_num}) is not Finished");

                    if (!string.Equals(t.status, "Ready", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(t.status, "InProgress", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Task status '{t.status}' is not scannable");

                    var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

                    // ✅ trả máy task hiện tại
                    if (!string.IsNullOrWhiteSpace(t.machine))
                        await _machineRepo.ReleaseAsync(t.machine!, release: 1);

                    t.status = "Finished";
                    t.end_time = now;

                    var log = new task_log
                    {
                        task_id = t.task_id,
                        scanner_id = req.scanner_id,
                        scanned_code = req.token,
                        action_type = "Finished",
                        qty_good = req.qty_good ?? 0,
                        qty_bad = req.qty_bad ?? 0,
                        operator_id = req.operator_id,
                        log_time = now
                    };

                    await _logRepo.AddAsync(log);

                    var next = await _taskRepo.GetNextTaskAsync(t.prod_id.Value, t.seq_num.Value);
                    if (next != null && string.Equals(next.status, "Unassigned", StringComparison.OrdinalIgnoreCase))
                    {
                        next.status = "Ready";
                        next.start_time ??= now;

                        // ✅ giữ máy cho task next
                        if (!string.IsNullOrWhiteSpace(next.machine))
                            await _machineRepo.AllocateAsync(next.machine!, need: 1);
                    }

                    await _taskRepo.SaveChangesAsync();
                    await _logRepo.SaveChangesAsync();

                    if (t.prod_id.HasValue)
                    {
                        await _prodRepo.TryCloseProductionIfCompletedAsync(
                            t.prod_id.Value, now, CancellationToken.None);
                    }

                    await tx.CommitAsync();

                    return new ScanTaskResult
                    {
                        task_id = t.task_id,
                        prod_id = t.prod_id,
                        message = "Finished & progressed"
                    };
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            });
        }
    }
}
