using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Repositories
{
    public class TaskRepository : ITaskRepository
    {
        private const int TokenQtyMax = int.MaxValue;
        private readonly AppDbContext _db;

        public TaskRepository(AppDbContext db)
        {
            _db = db;
        }

        public Task AddRangeAsync(IEnumerable<task> tasks)
        {
            _db.tasks.AddRange(tasks);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync()
            => _db.SaveChangesAsync();

        public Task SaveChangesAsync(CancellationToken ct)
            => _db.SaveChangesAsync(ct);

        public Task<task?> GetByIdAsync(int taskId)
            => _db.tasks.FirstOrDefaultAsync(x => x.task_id == taskId);

        public Task<task?> GetByIdWithProcessAsync(int taskId, CancellationToken ct = default)
            => _db.tasks
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

        public Task<task?> GetNextTaskAsync(int prodId, int currentSeqNum)
            => _db.tasks
                .Where(x => x.prod_id == prodId && x.seq_num > currentSeqNum)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .FirstOrDefaultAsync();

        public Task<task?> GetPrevTaskAsync(int prodId, int seqNum)
            => _db.tasks
                .Where(x => x.prod_id == prodId && x.seq_num < seqNum)
                .OrderByDescending(x => x.seq_num)
                .ThenByDescending(x => x.task_id)
                .FirstOrDefaultAsync();

        public async Task<List<task>> GetTasksByProductionAsync(int prodId, CancellationToken ct = default)
        {
            return await _db.tasks
                .Where(x => x.prod_id == prodId)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);
        }

        public async Task<List<task>> GetTasksByProductionWithProcessAsync(int prodId, CancellationToken ct = default)
        {
            return await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == prodId)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);
        }

        public async Task<task?> GetFirstTaskByProductionAsync(int prodId, CancellationToken ct = default)
        {
            return await _db.tasks
                .Where(x => x.prod_id == prodId)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<List<TaskFlowDto>> GetTasksWithCodesByProductionAsync(int prodId, CancellationToken ct = default)
        {
            return await _db.tasks
                .AsNoTracking()
                .Where(x => x.prod_id == prodId)
                .Select(x => new TaskFlowDto
                {
                    task_id = x.task_id,
                    prod_id = x.prod_id ?? 0,
                    seq_num = x.seq_num,
                    status = x.status,
                    machine = x.machine,
                    process_code = x.process != null ? x.process.process_code : null
                })
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);
        }

        public async Task<bool> SetTaskReadyAsync(int taskId, CancellationToken ct = default)
        {
            var now = AppTime.NowVnUnspecified();

            var t = await _db.tasks
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (t == null)
                return false;

            t.status = "Ready";
            t.start_time ??= now;

            await TryAllocateMachineWhenReadyAsync(t, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task MarkTaskReadyAsync(int taskId, DateTime now, CancellationToken ct = default)
        {
            var t = await _db.tasks
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (t == null)
                throw new InvalidOperationException("Task not found");

            t.status = "Ready";
            t.start_time ??= now;

            await TryAllocateMachineWhenReadyAsync(t, ct);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<bool> PromoteInitialTasksAsync(int prodId, DateTime now, CancellationToken ct = default)
        {
            var tasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == prodId)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return false;

            var promoted = false;

            var initialTasks = tasks
                .Where(x => !string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase))
                .Where(x => ProductionFlowHelper.IsInitialParallel(x.process?.process_code))
                .ToList();

            if (initialTasks.Count == 0)
            {
                var first = tasks.FirstOrDefault(x =>
                    !string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase));

                if (first != null)
                    initialTasks.Add(first);
            }

            foreach (var t in initialTasks)
            {
                if (string.Equals(t.status, "Ready", StringComparison.OrdinalIgnoreCase))
                    continue;

                t.status = "Ready";
                t.start_time ??= now;
                await TryAllocateMachineWhenReadyAsync(t, ct);
                promoted = true;
            }

            return promoted;
        }

        public Task<bool> PromoteFirstTaskToReadyAsync(int prodId, DateTime now, CancellationToken ct = default)
            => PromoteInitialTasksAsync(prodId, now, ct);

        public async Task<bool> PromoteAllTasksAfterRaloAsync(int prodId, DateTime now, CancellationToken ct = default)
        {
            var tasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == prodId)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return false;

            var ralo = tasks.FirstOrDefault(x => ProductionFlowHelper.IsRalo(x.process?.process_code));
            if (ralo == null || !ralo.seq_num.HasValue)
                return false;

            var promoted = false;

            foreach (var t in tasks.Where(x =>
                         x.seq_num.HasValue &&
                         x.seq_num.Value > ralo.seq_num.Value &&
                         !string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(t.status, "Ready", StringComparison.OrdinalIgnoreCase))
                    continue;

                t.status = "Ready";
                t.start_time ??= now;
                await TryAllocateMachineWhenReadyAsync(t, ct);
                promoted = true;
            }

            return promoted;
        }

        public async Task<bool> PromoteNextTaskToReadyAsync(int currentTaskId, DateTime now, CancellationToken ct = default)
        {
            var current = await _db.tasks
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == currentTaskId, ct);

            if (current == null || !current.prod_id.HasValue)
                return false;

            var tasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == current.prod_id.Value)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return false;

            var currentSeq = current.seq_num ?? int.MinValue;

            var next = tasks
                .Where(x =>
                    x.task_id != current.task_id &&
                    !string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) &&
                    (x.seq_num ?? int.MaxValue) > currentSeq)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .FirstOrDefault();

            if (next == null)
                return false;

            var nextSeq = next.seq_num ?? int.MaxValue;

            var hasPreviousUnfinished = tasks.Any(x =>
                x.task_id != next.task_id &&
                (x.seq_num ?? int.MaxValue) < nextSeq &&
                !string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase));

            if (hasPreviousUnfinished)
                return false;

            if (string.Equals(next.status, "Ready", StringComparison.OrdinalIgnoreCase))
                return false;

            next.status = "Ready";
            next.start_time ??= now;

            await TryAllocateMachineWhenReadyAsync(next, ct);
            return true;
        }

        private async Task<int?> GetPreviousGroupedOutputQtyCapAsync(
    task currentTask,
    production prod,
    CancellationToken ct)
        {
            if (!prod.order_id.HasValue)
                return null;

            if (!currentTask.prod_id.HasValue)
                return null;

            if (!currentTask.seq_num.HasValue)
                return null;

            var orderId = prod.order_id.Value;
            var singleProdId = currentTask.prod_id.Value;
            var currentSeq = currentTask.seq_num.Value;

            /*
             * Không dùng:
             * string.Equals(x.status, "Cancelled", StringComparison.OrdinalIgnoreCase)
             * trong IQueryable vì EF Core không translate được StringComparison.
             *
             * Dùng EF.Functions.ILike cho PostgreSQL/Npgsql để so sánh không phân biệt hoa thường.
             */
            var previousGroup = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.single_prod_id == singleProdId &&
                    x.order_id == orderId &&
                    x.group_task != null &&
                    x.group_task.seq_num.HasValue &&
                    x.group_task.seq_num.Value < currentSeq &&
                    (
                        x.status == null ||
                        !EF.Functions.ILike(x.status.Trim(), "Cancelled")
                    ))
                .OrderByDescending(x => x.group_task!.seq_num)
                .ThenByDescending(x => x.group_task_id)
                .Select(x => new
                {
                    x.group_task_id,
                    x.process_code
                })
                .FirstOrDefaultAsync(ct);

            if (previousGroup == null)
                return null;

            var qty = await _db.task_qtys
                .AsNoTracking()
                .Where(x =>
                    x.group_task_id == previousGroup.group_task_id &&
                    x.order_id == orderId &&
                    x.process_code == previousGroup.process_code)
                .SumAsync(x => x.qty_good, ct);

            if (qty <= 0)
                return null;

            return qty;
        }

        public async Task<TaskQtyPolicyDto?> GetQtyPolicyAsync(int taskId, CancellationToken ct = default)
        {
            var taskRow = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (taskRow == null || !taskRow.prod_id.HasValue)
                return null;

            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == taskRow.prod_id.Value, ct);

            if (prod == null || !prod.order_id.HasValue)
                return null;

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == prod.order_id.Value)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            if (req == null)
                return null;

            cost_estimate? est = null;

            if (req.accepted_estimate_id.HasValue && req.accepted_estimate_id.Value > 0)
            {
                est = await _db.cost_estimates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == req.accepted_estimate_id.Value &&
                        x.order_request_id == req.order_request_id,
                        ct);
            }

            est ??= await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == req.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            var route = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == taskRow.prod_id.Value)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (route.Count == 0)
                return null;

            var currentIndex = route.FindIndex(x => x.task_id == taskId);
            if (currentIndex < 0)
                currentIndex = 0;

            var stage = route[currentIndex];

            var pcode = Norm(stage.process?.process_code);
            var pname = string.IsNullOrWhiteSpace(stage.process?.process_name)
                ? pcode
                : stage.process!.process_name!;

            var orderQty = SafePositive(req.quantity ?? 0, 1);

            var sheetsRequired = Math.Max(est?.sheets_required ?? 0, 0);
            var sheetsWaste = Math.Max(est?.sheets_waste ?? 0, 0);
            var sheetsTotal = Math.Max(est?.sheets_total ?? 0, sheetsRequired + sheetsWaste);
            var nUp = SafePositive(est?.n_up ?? 0, 1);
            var numberOfPlates = SafePositive(req.number_of_plates ?? 0, 1);

            if (sheetsRequired <= 0)
                sheetsRequired = Math.Max(1, (int)Math.Ceiling(orderQty / (decimal)nUp));

            if (sheetsTotal <= 0)
                sheetsTotal = sheetsRequired + sheetsWaste;

            if (sheetsTotal <= 0)
                sheetsTotal = sheetsRequired;

            if (sheetsTotal <= 0)
                sheetsTotal = 1;

            var routeCodes = route
    .Select(x => (string?)x.process?.process_code)
    .ToList();

            var bothQtyContext = await ResolveBothProductionQtyContextAsync(
                prod,
                orderQty,
                routeCodes,
                ct);

            var qtyProfile = StageQuantityHelper.BuildPolicy(
                currentCode: pcode,
                currentStageIndex: currentIndex,
                routeProcessCodes: routeCodes,
                sheetsTotal: sheetsTotal,
                nUp: nUp,
                numberOfPlates: numberOfPlates,
                tokenQtyMax: TokenQtyMax);

            var minAllowed = qtyProfile.MinAllowed;
            var maxAllowed = qtyProfile.MaxAllowed;
            var suggestedQty = qtyProfile.SuggestedQty;
            var happyCaseQty = qtyProfile.SuggestedQty;

            var previousGroupedCap = await GetPreviousGroupedOutputQtyCapAsync(
    stage,
    prod,
    ct);

            if (previousGroupedCap.HasValue && previousGroupedCap.Value > 0)
            {
                maxAllowed = Math.Min(maxAllowed, previousGroupedCap.Value);

                if (maxAllowed <= 0)
                    maxAllowed = previousGroupedCap.Value;

                minAllowed = 1;
                suggestedQty = maxAllowed;
                happyCaseQty = suggestedQty;
            }

            // BOTH:
            if (bothQtyContext.IsCoveredBySub(currentIndex) &&
                !string.Equals(pcode, "RALO", StringComparison.OrdinalIgnoreCase))
            {
                minAllowed = ScaleQtyByRatio(minAllowed, bothQtyContext.NvlRatio);
                maxAllowed = ScaleQtyByRatio(maxAllowed, bothQtyContext.NvlRatio);
                suggestedQty = ScaleQtyByRatio(suggestedQty, bothQtyContext.NvlRatio);
                happyCaseQty = suggestedQty;

                if (maxAllowed < minAllowed)
                    maxAllowed = minAllowed;

                suggestedQty = Math.Clamp(suggestedQty, minAllowed, maxAllowed);
            }

            // Ví dụ sub có CAT,IN; tới PHU phải full estimate,
            var shouldApplyPreviousActualCap =
                !bothQtyContext.IsFirstStageAfterSub(currentIndex);

            var previousActualCap = await GetPreviousFinishedQtyGoodCapAsync(
                route,
                currentIndex,
                ct);

            if (shouldApplyPreviousActualCap &&
                previousActualCap.HasValue &&
                previousActualCap.Value > 0)
            {
                maxAllowed = Math.Min(maxAllowed, previousActualCap.Value);
                if (maxAllowed <= 0)
                    maxAllowed = previousActualCap.Value;

                minAllowed = 1;
                suggestedQty = maxAllowed;
            }

            return new TaskQtyPolicyDto
            {
                task_id = taskId,
                process_code = pcode,
                process_name = pname,
                qty_unit = qtyProfile.QtyUnit,

                min_allowed = minAllowed,
                max_allowed = maxAllowed,
                suggested_qty = suggestedQty,

                order_qty = orderQty,
                sheets_required = sheetsRequired,
                sheets_waste = sheetsWaste,
                sheets_total = sheetsTotal,
                n_up = nUp,
                number_of_plates = numberOfPlates,

                happy_case_qty = happyCaseQty,
                stage_index = currentIndex,
                stage_count = route.Count
            };
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                try
                {
                    var result = await action(ct);
                    await tx.CommitAsync(ct);
                    return result;
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            });
        }

        public async Task<int> SuggestQtyGoodAsync(int taskId, CancellationToken ct = default)
        {
            var policy = await GetQtyPolicyAsync(taskId, ct);
            return policy?.suggested_qty ?? 1;
        }

        private async Task TryAllocateMachineWhenReadyAsync(task t, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(t.machine))
                return;

            var m = await _db.machines
                .FirstOrDefaultAsync(x => x.machine_code == t.machine && x.is_active, ct);

            if (m == null)
                return;

            m.busy_quantity ??= 0;
            m.free_quantity ??= (m.quantity - m.busy_quantity.Value);

            if (m.free_quantity <= 0)
                return;

            m.free_quantity -= 1;
            m.busy_quantity += 1;
        }

        public Task<task?> GetByIdTrackingAsync(int taskId, CancellationToken ct = default)
    => _db.tasks.FirstOrDefaultAsync(x => x.task_id == taskId, ct);

        public async Task MarkTaskFinishedFromStockAsync(int taskId, string reason, DateTime now, bool isTakenSubProduct, CancellationToken ct = default)
        {
            var entity = await _db.tasks.FirstOrDefaultAsync(x => x.task_id == taskId, ct);
            if (entity == null)
                return;

            entity.status = "Finished";
            entity.end_time = now;
            entity.reason = reason;
            entity.is_taken_sub_product = isTakenSubProduct;
        }

        private static string Norm(string? code)
            => (code ?? "").Trim().ToUpperInvariant();

        private static bool IsCutProcess(string? code)
        {
            var c = Norm(code);
            return c == "CAT" || c == "CUT";
        }

        private static bool ShouldCapByPreviousActual(string? currentCode, string? previousCode)
        {
            var current = Norm(currentCode);
            var previous = Norm(previousCode);

            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(previous))
                return false;

            if (ProductionFlowHelper.IsRalo(current))
                return false;

            if (IsCutProcess(current))
                return false;

            if (ProductionFlowHelper.IsRalo(previous))
                return false;

            return true;
        }

        private async Task<int?> GetPreviousFinishedQtyGoodCapAsync(
            List<task> route,
            int currentIndex,
            CancellationToken ct = default)
        {
            if (route == null || route.Count == 0)
                return null;

            if (currentIndex <= 0 || currentIndex >= route.Count)
                return null;

            var current = route[currentIndex];
            var previous = route[currentIndex - 1];

            var currentCode = current.process?.process_code;
            var previousCode = previous.process?.process_code;

            if (!ShouldCapByPreviousActual(currentCode, previousCode))
                return null;

            if (!string.Equals(previous.status, "Finished", StringComparison.OrdinalIgnoreCase)
                && previous.end_time == null)
            {
                return null;
            }

            var qty = await _db.task_logs
                .AsNoTracking()
                .Where(x =>
                    x.task_id == previous.task_id &&
                    x.action_type == "Finished")
                .SumAsync(x => x.qty_good ?? 0, ct);

            if (qty <= 0)
                return null;

            return qty;
        }

        private static int SafePositive(int value, int fallback = 1)
            => value > 0 ? value : fallback;

        private sealed class BothProductionQtyContext
        {
            public bool IsBoth { get; init; }
            public int SubLastIndex { get; init; } = -1;
            public decimal NvlRatio { get; init; } = 1m;

            public bool IsCoveredBySub(int stageIndex)
                => IsBoth && SubLastIndex >= 0 && stageIndex <= SubLastIndex;

            public bool IsFirstStageAfterSub(int stageIndex)
                => IsBoth && SubLastIndex >= 0 && stageIndex == SubLastIndex + 1;
        }

        private static string NormBothProcessCode(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static HashSet<string> ParseBothProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormBothProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static int ScaleQtyByRatio(int qty, decimal ratio)
        {
            if (qty <= 0)
                return qty;

            if (ratio <= 0m)
                return 1;

            if (ratio >= 1m)
                return qty;

            return Math.Max(1, (int)Math.Ceiling(qty * ratio));
        }

        private async Task<BothProductionQtyContext> ResolveBothProductionQtyContextAsync(
            production prod,
            int orderQty,
            IReadOnlyList<string?> routeProcessCodes,
            CancellationToken ct)
        {
            if (!string.Equals(prod.prod_method, "BOTH", StringComparison.OrdinalIgnoreCase))
                return new BothProductionQtyContext();

            if (!prod.sub_product_id.HasValue || prod.sub_product_id.Value <= 0)
                return new BothProductionQtyContext();

            orderQty = SafePositive(orderQty, 1);

            var nvlQty = prod.nvl_qty > 0
                ? prod.nvl_qty
                : Math.Max(orderQty - prod.sub_product_used_qty, 0);

            if (nvlQty <= 0)
                return new BothProductionQtyContext();

            var subProcess = await _db.sub_products
                .AsNoTracking()
                .Where(x => x.id == prod.sub_product_id.Value)
                .Select(x => x.product_process)
                .FirstOrDefaultAsync(ct);

            var subCodes = ParseBothProcessCodes(subProcess);
            if (subCodes.Count == 0)
                return new BothProductionQtyContext();

            var subLastIndex = -1;

            for (var i = 0; i < routeProcessCodes.Count; i++)
            {
                var routeCode = NormBothProcessCode(routeProcessCodes[i]);
                if (subCodes.Contains(routeCode))
                    subLastIndex = i;
            }

            if (subLastIndex < 0)
                return new BothProductionQtyContext();

            var ratio = Math.Clamp((decimal)nvlQty / orderQty, 0m, 1m);

            return new BothProductionQtyContext
            {
                IsBoth = true,
                SubLastIndex = subLastIndex,
                NvlRatio = ratio
            };
        }
    }
}