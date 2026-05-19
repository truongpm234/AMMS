using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class TaskService : ITaskService
    {
        private readonly ITaskRepository _taskRepo;
        private readonly AppDbContext _db;

        public TaskService(ITaskRepository taskRepo, AppDbContext db)
        {
            _taskRepo = taskRepo;
            _db = db;
        }

        public async Task<bool> SetTaskReadyAsync(int taskId, CancellationToken ct = default)
        {
            var current = await _taskRepo.GetByIdWithProcessAsync(taskId, ct);
            if (current == null)
                return false;

            if (!current.prod_id.HasValue || !current.seq_num.HasValue)
                throw new InvalidOperationException("Task thiếu prod_id hoặc seq_num.");

            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == current.prod_id.Value, ct);

            if (prod == null)
                throw new InvalidOperationException("Không tìm thấy production của task.");

            var isGroupProduction = string.Equals(
                prod.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(prod.status, "InProcessing", StringComparison.OrdinalIgnoreCase))
            {
                if (isGroupProduction)
                    throw new InvalidOperationException("Production ghép chưa được bắt đầu.");

                throw new InvalidOperationException("Quá trình sản xuất chưa được quản lí phê duyệt bắt đầu.");
            }

            if (isGroupProduction)
            {
                var activeOrderCount = await _db.prod_orders
                    .AsNoTracking()
                    .CountAsync(x => x.prod_id == prod.prod_id && x.status == "Active", ct);

                if (activeOrderCount < 2)
                    throw new InvalidOperationException("Production ghép cần ít nhất 2 order active.");

                await EnsureGroupPrerequisiteFinishedAsync(current.task_id, ct);
            }
            else
            {
                if (!prod.order_id.HasValue)
                    throw new InvalidOperationException("Production chưa gắn với order.");

                var order = await _db.orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == prod.order_id.Value, ct);

                if (order == null)
                    throw new InvalidOperationException("Không tìm thấy order của production.");

                if (!order.layout_confirmed)
                    throw new InvalidOperationException("Order chưa xác nhận layout, không thể bắt đầu công đoạn.");

                if (!order.is_production_ready)
                    throw new InvalidOperationException("Order chưa được xác nhận sẵn sàng sản xuất, không thể bắt đầu công đoạn.");
            }

            if (string.Equals(current.status, "Finished", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Task đã Finished, không thể chuyển lại Ready.");

            var dep = await ProductionDependencyValidator.CheckTaskCanStartAsync(
                    _db,
                    taskId,
                    ct);

            if (!dep.can_start)
            {
                throw new InvalidOperationException(
                    "Chưa thể chuyển task sang Ready vì công đoạn trước đó chưa hoàn thành. " +
                    dep.message);
            }

            if (string.Equals(current.status, "Ready", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(current.status, "GroupedWaiting", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Task này đã được ghép vào production chung, không thể chạy riêng.");
            }

            var flowTasks = await _taskRepo.GetTasksByProductionWithProcessAsync(current.prod_id.Value, ct);
            if (flowTasks.Count == 0)
                throw new InvalidOperationException("Không tìm thấy flow task của production.");

            var currentFlow = flowTasks.FirstOrDefault(x => x.task_id == current.task_id);
            if (currentFlow == null)
                throw new InvalidOperationException("Task không thuộc flow hiện tại.");

            var currentCode = ProductionFlowHelper.Norm(currentFlow.process?.process_code);
            var isInitialParallel = ProductionFlowHelper.IsInitialParallel(currentCode);

            if (!string.IsNullOrWhiteSpace(current.status) &&
                !string.Equals(current.status, "Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Chỉ task đang ở trạng thái Unassigned mới được bấm Ready. Hiện tại: {current.status}");
            }

            if (!isInitialParallel)
            {
                var currentSeq = currentFlow.seq_num ?? int.MaxValue;

                var previousUnfinished = flowTasks
                    .Where(x => x.task_id != currentFlow.task_id)
                    .Where(x => (x.seq_num ?? int.MaxValue) < currentSeq)
                    .Where(x => !string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => x.task_id)
                    .FirstOrDefault();

                if (previousUnfinished != null)
                {
                    var prevName = previousUnfinished.name ?? previousUnfinished.process?.process_name ?? "previous task";
                    var prevCode = previousUnfinished.process?.process_code ?? "";

                    throw new InvalidOperationException(
                        $"Công đoạn trước [{prevCode} - {prevName}] chưa Finished nên chưa thể bắt đầu công đoạn này.");
                }
            }

            var now = AppTime.NowVnUnspecified();

            await _taskRepo.MarkTaskReadyAsync(taskId, now, ct);
            return true;
        }

        private static bool StatusEquals(string? status, string expected)
        {
            return string.Equals(
                status?.Trim(),
                expected,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinishedStatus(string? status, DateTime? endTime = null)
        {
            return StatusEquals(status, "Finished") || endTime != null;
        }

        private async Task EnsureGroupPrerequisiteFinishedAsync(
            int groupTaskId,
            CancellationToken ct)
        {
            var groupTask = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == groupTaskId, ct);

            if (groupTask == null)
                throw new InvalidOperationException("Không tìm thấy group task.");

            if (!groupTask.seq_num.HasValue)
                throw new InvalidOperationException("Group task thiếu seq_num.");

            var groupSeq = groupTask.seq_num.Value;
            var groupCode = NormCode(groupTask.process?.process_code);

            var links = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.group_task_id == groupTaskId &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .ToListAsync(ct);

            if (links.Count == 0)
                return;

            foreach (var link in links)
            {
                var notFinishedBefore = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.process)
                    .Where(x =>
                        x.prod_id == link.single_prod_id &&
                        x.seq_num.HasValue &&
                        x.seq_num.Value < groupSeq &&
                        (
                            x.status == null ||
                            x.status.ToUpper() != "FINISHED"
                        ))
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => x.task_id)
                    .ToListAsync(ct);

                if (notFinishedBefore.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Order {link.order_id} chưa hoàn thành công đoạn riêng trước công đoạn ghép {groupCode}. " +
                        $"Còn thiếu: {string.Join(",", notFinishedBefore.Select(x => x.process?.process_code ?? x.name))}");
                }

                var previousGroupLinks = await _db.task_links
                    .AsNoTracking()
                    .Where(x =>
                        x.single_prod_id == link.single_prod_id &&
                        x.group_task_id != groupTaskId &&
                        (
                            x.status == null ||
                            x.status.ToUpper() != "CANCELLED"
                        ))
                    .Select(x => new
                    {
                        x.group_task_id,
                        x.order_id,
                        x.process_code
                    })
                    .ToListAsync(ct);

                if (previousGroupLinks.Count == 0)
                    continue;

                var prevGroupTaskIds = previousGroupLinks
                    .Select(x => x.group_task_id)
                    .Distinct()
                    .ToList();

                var prevGroupTasks = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.process)
                    .Where(x => prevGroupTaskIds.Contains(x.task_id))
                    .ToDictionaryAsync(x => x.task_id, ct);

                foreach (var prev in previousGroupLinks)
                {
                    if (!prevGroupTasks.TryGetValue(prev.group_task_id, out var prevGroupTask))
                        continue;

                    if (!prevGroupTask.seq_num.HasValue)
                        continue;

                    if (prevGroupTask.seq_num.Value >= groupSeq)
                        continue;

                    if (!IsFinishedStatus(prevGroupTask.status, prevGroupTask.end_time))
                    {
                        throw new InvalidOperationException(
                            $"Order {link.order_id} chưa hoàn thành công đoạn ghép trước đó: {prev.process_code}.");
                    }

                    var prevProcessCode = NormCode(prev.process_code);

                    var hasQty = await _db.task_qtys
                        .AsNoTracking()
                        .AnyAsync(x =>
                            x.group_task_id == prev.group_task_id &&
                            x.order_id == link.order_id &&
                            x.process_code != null &&
                            x.process_code.Trim().ToUpper() == prevProcessCode &&
                            x.qty_good > 0,
                            ct);

                    if (!hasQty)
                    {
                        throw new InvalidOperationException(
                            $"Order {link.order_id} đã xong group task {prev.process_code} nhưng chưa có sản lượng phân bổ.");
                    }
                }
            }
        }

        private static string NormCode(string? code)
        {
            return (code ?? "").Trim().ToUpperInvariant();
        }

        public async Task<FinishTasksFromStockResponse> FinishTasksFromStockAsync(List<int> taskIds, int? scannedByUserId = null, CancellationToken ct = default)
        {
            const string fixedReason = "Bán thành phẩm đã có sẵn trong kho";

            if (taskIds == null || taskIds.Count == 0)
                throw new InvalidOperationException("task_ids is required.");

            var distinctTaskIds = taskIds
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            var result = new FinishTasksFromStockResponse();
            var now = AppTime.NowVnUnspecified();

            foreach (var taskId in distinctTaskIds)
            {
                var task = await _taskRepo.GetByIdTrackingAsync(taskId, ct);

                if (task == null)
                {
                    result.not_found_task_ids.Add(taskId);
                    continue;
                }

                if (string.Equals(task.status, "Finished", StringComparison.OrdinalIgnoreCase))
                {
                    result.already_finished_task_ids.Add(taskId);
                    continue;
                }

                await _taskRepo.MarkTaskFinishedFromStockAsync(taskId, fixedReason, now, true, ct);
                result.finished_task_ids.Add(taskId);
            }

            await _taskRepo.SaveChangesAsync(ct);

            return result;
        }
    }
}