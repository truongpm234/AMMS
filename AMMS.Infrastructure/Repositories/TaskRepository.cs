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

            var orderQty = await ResolvePolicyProductionQtyAsync(prod, req, ct);

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

            if (est == null)
                return null;

            /*
             * FIX SUB SINGLE:
             * Nếu production là SINGLE + SUB thì các công đoạn sau BTP
             * phải validate theo actual output của công đoạn trước,
             * không validate theo estimate/NVL.
             *
             * Ví dụ:
             * IN actual = 2835
             * PHU max_allowed phải = 2835
             * không được dùng estimate 2145.
             */
            var singleSubActualPolicy = await TryBuildSingleSubActualPreviousPolicyAsync(
                taskId: taskId,
                stage: stage,
                prod: prod,
                req: req,
                est: est,
                route: route,
                currentIndex: currentIndex,
                routeCodes: routeCodes,
                orderQty: orderQty,
                sheetsRequired: sheetsRequired,
                sheetsWaste: sheetsWaste,
                sheetsTotal: sheetsTotal,
                nUp: nUp,
                numberOfPlates: numberOfPlates,
                ct: ct);

            if (singleSubActualPolicy != null)
                return singleSubActualPolicy;

            var subPolicy = await TryBuildSubOrBothDownstreamQtyPolicyAsync(
    currentTask: stage,
    prod: prod,
    req: req,
    est: est,
    routeCodes: routeCodes,
    policyProductQty: orderQty,
    ct: ct);

            if (subPolicy != null)
                return subPolicy;

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
                happy_case_qty = happyCaseQty,

                order_qty = orderQty,
                sheets_required = sheetsRequired,
                sheets_waste = sheetsWaste,
                sheets_total = sheetsTotal,
                n_up = nUp,
                number_of_plates = numberOfPlates,

                stage_index = currentIndex,
                stage_count = route.Count,

                production_output_qty = suggestedQty,
                production_output_unit = qtyProfile.QtyUnit,

                input_mode = stage.input_mode,

                allow_manual_input =
        string.Equals(stage.input_mode, "MANUAL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase),

                can_use_manual_input =
        string.Equals(stage.input_mode, "MANUAL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase),

                manual_input_optional = false,

                is_group_production = string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase),
                is_split_production = string.Equals(prod.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase),

                group_prod_id = string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase)
        ? prod.prod_id
        : null,

                split_prod_id = string.Equals(prod.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase)
        ? prod.prod_id
        : null,

                group_total_qty = prod.group_total_qty,

                manual_input_hint =
        string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase)
            ? "Task group cho phép nhập tay NVL, BTP input và output khi finish."
            : null
            };
        }

        private async Task<TaskQtyPolicyDto?> TryBuildSubOrBothDownstreamQtyPolicyAsync(
    task currentTask,
    production prod,
    order_request req,
    cost_estimate est,
    IReadOnlyList<string?> routeCodes,
    int policyProductQty,
    CancellationToken ct)
        {
            var method = NormPolicyCode(prod.prod_method);

            if (method != "SUB" && method != "BOTH")
                return null;

            var currentCode = NormPolicyCode(currentTask.process?.process_code);

            if (currentCode is not ("PHU" or "CAN" or "CAN_MANG" or "BOI" or "BE" or "DUT" or "DAN"))
                return null;

            var subCodes = await ResolveSubProductProcessCodesForPolicyAsync(
                prod,
                ct);

            if (method == "BOTH")
            {
                if (subCodes.Count == 0)
                    return null;

                if (!IsCurrentAfterSubBoundaryForPolicy(currentCode, routeCodes, subCodes))
                    return null;
            }

            if (method == "SUB")
            {
                if (subCodes.Count > 0 &&
                    !IsCurrentAfterSubBoundaryForPolicy(currentCode, routeCodes, subCodes))
                {
                    return null;
                }
            }

            decimal productQty = policyProductQty > 0
                ? policyProductQty
                : Math.Max(
                    (decimal)(prod.sub_product_used_qty + prod.nvl_qty),
                    1m);

            var nUp = est.n_up > 0 ? est.n_up : 1;

            var sheetsBase = est.sheets_required > 0
                ? est.sheets_required
                : (int)Math.Ceiling(productQty / (decimal)nUp);

            var stageQty = SubProductionQuantityHelper.ResolveStageQty(
                currentProcessCode: currentCode,
                routeProcessCodes: routeCodes,
                productQty: productQty,
                nUp: nUp,
                explicitSheetsBase: sheetsBase,
                coatingType: est.coating_type);

            var route = (routeCodes ?? Array.Empty<string?>())
                .Select(NormPolicyCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var stageIndex = route.FindIndex(x =>
                string.Equals(x, currentCode, StringComparison.OrdinalIgnoreCase));

            /*
             * Base suggested là output_qty theo quy ước hao hụt.
             */
            var suggested = Math.Max((int)Math.Ceiling(stageQty.output_qty), 1);

            /*
             * Nếu công đoạn trước đã có actual output, thì giới hạn không được vượt quá actual previous.
             * Nhưng với BOTH ở công đoạn đầu sau boundary, actual previous chỉ là phần NVL,
             * nên phải cộng thêm BTP đã dùng.
             */
            var previousActual = await ResolvePreviousActualQtyForPolicyAsync(
                prod,
                currentTask,
                route,
                stageIndex,
                ct);

            if (method == "SUB")
            {
                if (previousActual > 0)
                    suggested = Math.Max((int)Math.Ceiling(previousActual), 1);
            }

            if (method == "BOTH")
            {
                var isFirstAfterSub = IsFirstStageAfterSubBoundaryForPolicy(
                    currentCode,
                    routeCodes,
                    subCodes);

                if (isFirstAfterSub)
                {
                    var combined = previousActual + Math.Max(prod.sub_product_used_qty, 0);

                    if (combined > 0)
                        suggested = Math.Max((int)Math.Ceiling(combined), 1);
                }
                else if (previousActual > 0)
                {
                    suggested = Math.Max((int)Math.Ceiling(previousActual), 1);
                }
            }

            return new TaskQtyPolicyDto
            {
                task_id = currentTask.task_id,

                process_code = currentTask.process?.process_code ?? currentCode,
                process_name = currentTask.process?.process_name ?? currentCode,

                qty_unit = "sp",

                min_allowed = 1,
                max_allowed = suggested,
                suggested_qty = suggested,
                happy_case_qty = suggested,

                order_qty = (int)Math.Ceiling(productQty),

                sheets_required = est.sheets_required,
                sheets_waste = est.sheets_waste,
                sheets_total = est.sheets_total,
                n_up = nUp,
                number_of_plates = req.number_of_plates ?? 0,

                stage_index = stageIndex,
                stage_count = routeCodes.Count,

                production_output_qty = suggested,
                production_output_unit = "sp",

                input_mode = currentTask.input_mode,

                allow_manual_input = true,
                can_use_manual_input = true,
                manual_input_optional = false,

                is_group_production = string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase),
                is_split_production = string.Equals(prod.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase),

                group_prod_id = string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase)
                    ? prod.prod_id
                    : null,

                split_prod_id = string.Equals(prod.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase)
                    ? prod.prod_id
                    : null,

                group_total_qty = prod.group_total_qty,

                manual_input_hint =
                    $"{method} downstream: suggested/max theo dữ liệu production và actual previous nếu đã có. " +
                    $"ProductQty={stageQty.product_qty}, InputQty={stageQty.input_qty}, OutputQty={stageQty.output_qty}, " +
                    $"PreviousActual={Math.Round(previousActual, 4)}."
            };
        }

        private async Task<decimal> ResolvePreviousActualQtyForPolicyAsync(
    production prod,
    task currentTask,
    IReadOnlyList<string> normalizedRoute,
    int currentStageIndex,
    CancellationToken ct)
        {
            if (prod == null || currentTask == null)
                return 0m;

            if (currentStageIndex <= 0 || currentStageIndex >= normalizedRoute.Count)
                return 0m;

            var previousCode = normalizedRoute[currentStageIndex - 1];

            if (string.IsNullOrWhiteSpace(previousCode))
                return 0m;

            /*
             * Ưu tiên actual theo task trong cùng production.
             */
            if (currentTask.prod_id.HasValue)
            {
                var previousTaskIds = await (
                    from t in _db.tasks.AsNoTracking()
                    join pp0 in _db.product_type_processes.AsNoTracking()
                        on t.process_id equals pp0.process_id into ppj
                    from pp in ppj.DefaultIfEmpty()
                    where t.prod_id == currentTask.prod_id.Value
                          && t.task_id != currentTask.task_id
                          && pp != null
                          && pp.process_code != null
                          && pp.process_code.Trim().ToUpper() == previousCode
                    select t.task_id
                )
                .Distinct()
                .ToListAsync(ct);

                if (previousTaskIds.Count > 0)
                {
                    var qty = await _db.task_logs
                        .AsNoTracking()
                        .Where(x =>
                            x.task_id.HasValue &&
                            previousTaskIds.Contains(x.task_id.Value) &&
                            x.action_type == "Finished")
                        .SumAsync(x => x.qty_good ?? 0, ct);

                    if (qty > 0)
                        return qty;
                }
            }

            /*
             * Nếu previous là GROUP task, sản lượng nằm trong task_qtys.
             */
            if (prod.order_id.HasValue)
            {
                var groupQty = await _db.task_qtys
                    .AsNoTracking()
                    .Where(x =>
                        x.order_id == prod.order_id.Value &&
                        x.process_code != null &&
                        x.process_code.Trim().ToUpper() == previousCode &&
                        x.qty_good > 0)
                    .OrderByDescending(x => x.created_at)
                    .Select(x => (decimal?)x.qty_good)
                    .FirstOrDefaultAsync(ct);

                if (groupQty.HasValue && groupQty.Value > 0)
                    return groupQty.Value;
            }

            return 0m;
        }

        private static bool IsFirstStageAfterSubBoundaryForPolicy(
            string? currentProcessCode,
            IReadOnlyList<string?> routeCodes,
            IReadOnlyList<string> subCodes)
        {
            var route = (routeCodes ?? Array.Empty<string?>())
                .Select(NormPolicyCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (route.Count == 0 || subCodes == null || subCodes.Count == 0)
                return false;

            var current = NormPolicyCode(currentProcessCode);

            var currentIndex = route.FindIndex(x =>
                string.Equals(x, current, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
                return false;

            var subIndexes = subCodes
                .Select(NormPolicyCode)
                .Select(code => route.FindIndex(x =>
                    string.Equals(x, code, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .ToList();

            if (subIndexes.Count == 0)
                return false;

            return currentIndex == subIndexes.Max() + 1;
        }

        private async Task<List<string>> ResolveSubProductProcessCodesForPolicyAsync(
            production prod,
            CancellationToken ct)
        {
            if (!prod.sub_product_id.HasValue || prod.sub_product_id.Value <= 0)
                return new List<string>();

            var csv = await _db.sub_products
                .AsNoTracking()
                .Where(x => x.id == prod.sub_product_id.Value)
                .Select(x => x.product_process)
                .FirstOrDefaultAsync(ct);

            return ParsePolicyRoute(csv);
        }

        private static bool IsCurrentAfterSubBoundaryForPolicy(
            string? currentProcessCode,
            IReadOnlyList<string?> routeCodes,
            IReadOnlyList<string> subCodes)
        {
            var route = (routeCodes ?? Array.Empty<string?>())
                .Select(NormPolicyCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (route.Count == 0)
                return false;

            var current = NormPolicyCode(currentProcessCode);

            var currentIndex = route.FindIndex(x =>
                string.Equals(x, current, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
                return false;

            var subIndexes = subCodes
                .Select(NormPolicyCode)
                .Select(code => route.FindIndex(x =>
                    string.Equals(x, code, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .ToList();

            if (subIndexes.Count == 0)
                return false;

            return currentIndex > subIndexes.Max();
        }

        private static List<string> ParsePolicyRoute(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormPolicyCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormPolicyCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
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

        private async Task<int> ResolvePolicyProductionQtyAsync(
    production prod,
    order_request req,
    CancellationToken ct)
        {
            /*
             * Số lượng để validate QR phải lấy theo production đã được duyệt,
             * không ưu tiên order_request.quantity nữa.
             *
             * Lý do:
             * - SUB: production có sub_product_used_qty.
             * - BOTH: production có sub_product_used_qty + nvl_qty.
             * - NVL: production có nvl_qty.
             * - order_request.quantity có thể là data cũ hoặc khác với production thực tế.
             */

            var prodKind = NormPolicyCode(prod.prod_kind);
            var method = NormPolicyCode(prod.prod_method);

            if (prodKind == "GROUP" && prod.group_total_qty > 0)
                return prod.group_total_qty;

            if (method == "SUB" && prod.sub_product_used_qty > 0)
                return prod.sub_product_used_qty;

            if (method == "BOTH")
            {
                var total = Math.Max(prod.sub_product_used_qty, 0) + Math.Max(prod.nvl_qty, 0);
                if (total > 0)
                    return total;
            }

            if (method == "NVL" && prod.nvl_qty > 0)
                return prod.nvl_qty;

            if (prod.order_id.HasValue && prod.order_id.Value > 0)
            {
                var itemQty = await _db.order_items
                    .AsNoTracking()
                    .Where(x => x.order_id == prod.order_id.Value)
                    .OrderBy(x => x.item_id)
                    .Select(x => (int?)x.quantity)
                    .FirstOrDefaultAsync(ct);

                if (itemQty.HasValue && itemQty.Value > 0)
                    return itemQty.Value;
            }

            if (req.quantity.HasValue && req.quantity.Value > 0)
                return req.quantity.Value;

            return 1;
        }

        private static bool IsSingleSubProduction(production prod)
        {
            var kind = Norm(prod.prod_kind);
            var method = Norm(prod.prod_method);

            /*
             * Có DB cũ có thể prod_kind null.
             * Nếu method = SUB và không phải GROUP/SPLIT thì coi là SINGLE.
             */
            var isSingle =
                string.IsNullOrWhiteSpace(kind) ||
                string.Equals(kind, "SINGLE", StringComparison.OrdinalIgnoreCase);

            return isSingle &&
                   string.Equals(method, "SUB", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<TaskQtyPolicyDto?> TryBuildSingleSubActualPreviousPolicyAsync(
            int taskId,
            task stage,
            production prod,
            order_request req,
            cost_estimate est,
            List<task> route,
            int currentIndex,
            IReadOnlyList<string?> routeCodes,
            int orderQty,
            int sheetsRequired,
            int sheetsWaste,
            int sheetsTotal,
            int nUp,
            int numberOfPlates,
            CancellationToken ct)
        {
            if (!IsSingleSubProduction(prod))
                return null;

            if (route == null || route.Count == 0)
                return null;

            if (currentIndex <= 0 || currentIndex >= route.Count)
                return null;

            var currentCode = Norm(stage.process?.process_code);

            if (string.IsNullOrWhiteSpace(currentCode))
                return null;

            /*
             * RALO và CẮT không dùng actual previous theo kiểu này.
             * Ví dụ RALO output có thể là số kẽm/plate,
             * CAT là bước chuyển đổi tờ, không nên ép theo previous.
             */
            if (ProductionFlowHelper.IsRalo(currentCode))
                return null;

            if (IsCutProcess(currentCode))
                return null;

            /*
             * Lấy actual output của công đoạn trước.
             * Hàm này đã có sẵn trong repository của bạn.
             * Với task PHU, previous = IN.
             * Nếu IN đã Finished từ SUB_PRODUCT và log qty_good = 2835,
             * thì previousActualQty = 2835.
             */
            var previousActualQty = await GetPreviousFinishedQtyGoodCapAsync(
                route,
                currentIndex,
                ct);

            if (!previousActualQty.HasValue || previousActualQty.Value <= 0)
                return null;

            var actualQty = previousActualQty.Value;

            var pcode = currentCode;
            var pname = string.IsNullOrWhiteSpace(stage.process?.process_name)
                ? pcode
                : stage.process!.process_name!;

            return new TaskQtyPolicyDto
            {
                task_id = taskId,

                process_code = pcode,
                process_name = pname,

                qty_unit = "sp",

                min_allowed = 1,
                max_allowed = actualQty,
                suggested_qty = actualQty,
                happy_case_qty = actualQty,

                /*
                 * order_qty vẫn giữ số lượng đơn hàng gốc để tracking.
                 * Số lượng validate/report lấy theo actualQty.
                 */
                order_qty = orderQty,

                sheets_required = sheetsRequired,
                sheets_waste = sheetsWaste,
                sheets_total = sheetsTotal,
                n_up = nUp,
                number_of_plates = numberOfPlates,

                stage_index = currentIndex,
                stage_count = route.Count,

                production_output_qty = actualQty,
                production_output_unit = "sp",

                input_mode = stage.input_mode,

                /*
                 * Không ép MANUAL.
                 * Task vẫn có thể chạy ESTIMATE,
                 * nhưng estimate của SUB SINGLE lúc này chính là actual previous qty.
                 */
                allow_manual_input = string.Equals(
                    stage.input_mode,
                    "MANUAL",
                    StringComparison.OrdinalIgnoreCase),

                can_use_manual_input = true,
                manual_input_optional = true,

                is_group_production = false,
                group_prod_id = null,
                group_total_qty = null,

                manual_input_hint =
                    $"SUB SINGLE: số lượng công đoạn {pcode} lấy theo actual output của công đoạn trước. " +
                    $"Actual previous qty = {actualQty}. Không dùng estimate/NVL để giới hạn max_allowed."
            };
        }
    }
}