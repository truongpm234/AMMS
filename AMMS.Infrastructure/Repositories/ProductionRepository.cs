using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Exceptions;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Text.Json;

namespace AMMS.Infrastructure.Repositories
{
    public class ProductionRepository : IProductionRepository
    {
        private readonly AppDbContext _db;
        private readonly ITaskRepository _taskRepo;

        public ProductionRepository(AppDbContext db, ITaskRepository taskRepo)
        {
            _db = db;
            _taskRepo = taskRepo;
        }

        public async Task<DateTime?> GetNearestDeliveryDateAsync()
        {
            return await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id
                where pr.actual_start_date != null
                      && pr.end_date == null
                      && o.delivery_date != null
                orderby o.delivery_date
                select o.delivery_date
            ).FirstOrDefaultAsync();
        }

        public Task AddAsync(production p)
        {
            _db.productions.Add(p);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync() => _db.SaveChangesAsync();

        public Task<production?> GetByIdForUpdateAsync(int prodId, CancellationToken ct = default)
        {
            return _db.productions
                .AsTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);
        }

        public async Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            NormalizePaging(ref page, ref pageSize);
            var skip = (page - 1) * pageSize;

            /*
             * Lấy từ productions làm bảng chính để có đủ:
             * - SINGLE
             * - GROUP
             * - SPLIT
             *
             * Sort theo prod_id desc để production mới nhất hiện đầu.
             */
            var baseRows = await (
                from pr in _db.productions.AsNoTracking()

                join o0 in _db.orders.AsNoTracking()
                    on pr.order_id equals (int?)o0.order_id into oj
                from o in oj.DefaultIfEmpty()

                orderby pr.prod_id descending

                select new BaseRow
                {
                    prod_id = pr.prod_id,

                    order_id = o != null ? o.order_id : null,

                    code = o != null ? o.code : pr.code,

                    delivery_date = o != null ? o.delivery_date : null,

                    product_type_id = pr.product_type_id,

                    production_status = pr.status,
                    order_status = o != null ? o.status : null,
                    sub_product_issue_file = pr.sub_product_issue_file,
                    customer_name = o == null ? "Production ghép" : "",
                    planned_end_date = pr.planned_end_date,
                    production_method = pr.prod_method,
                    is_full_process = pr.is_full_process,
                    sub_product_id = pr.sub_product_id,
                    sub_product_used_qty = pr.sub_product_used_qty,
                    nvl_qty = pr.nvl_qty,
                    gm_note = pr.gm_note,
                    mgr_note = pr.mgr_note,
                    production_approval_flow = pr.production_approval_flow,
                    prod_kind = pr.prod_kind,
                    production_code = pr.code,

                    group_process_codes = pr.group_process_codes,
                    group_total_qty = pr.group_total_qty,

                    created_at = pr.created_at,
                    planned_start_date = pr.planned_start_date,
                    actual_start_date = pr.actual_start_date,
                    end_date = pr.end_date,
                    first_item_product_name =
                        o == null
                            ? "Lệnh sản xuất ghép"
                            : _db.order_items.AsNoTracking()
                                .Where(i => i.order_id == o.order_id)
                                .OrderBy(i => i.item_id)
                                .Select(i => i.product_name)
                                .FirstOrDefault(),

                    first_item_production_process =
                        o == null
                            ? pr.group_process_codes
                            : _db.order_items.AsNoTracking()
                                .Where(i => i.order_id == o.order_id)
                                .OrderBy(i => i.item_id)
                                .Select(i => i.production_process)
                                .FirstOrDefault(),

                    first_item_quantity =
                        o == null
                            ? pr.group_total_qty
                            : _db.order_items.AsNoTracking()
                                .Where(i => i.order_id == o.order_id)
                                .OrderBy(i => i.item_id)
                                .Select(i => (int?)i.quantity)
                                .FirstOrDefault()
                }
            )
            .Skip(skip)
            .Take(pageSize + 1)
            .ToListAsync(ct);

            var hasNext = baseRows.Count > pageSize;

            if (hasNext)
                baseRows.RemoveAt(baseRows.Count - 1);

            if (baseRows.Count == 0)
            {
                return new PagedResultLite<ProducingOrderCardDto>
                {
                    Page = page,
                    PageSize = pageSize,
                    HasNext = false,
                    Data = new List<ProducingOrderCardDto>()
                };
            }

            var prodIds = baseRows
                .Select(x => x.prod_id)
                .Distinct()
                .ToList();

            var orderIds = baseRows
                .Where(x => x.order_id.HasValue)
                .Select(x => x.order_id!.Value)
                .Distinct()
                .ToList();

            var baseGroupProdIds = baseRows
                .Where(x => string.Equals(x.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.prod_id)
                .Distinct()
                .ToList();

            var customerRows = orderIds.Count == 0
                ? new List<CustomerRow>()
                : await (
                    from o in _db.orders.AsNoTracking()

                    join q in _db.quotes.AsNoTracking()
                        on o.quote_id equals q.quote_id into qj
                    from q in qj.DefaultIfEmpty()

                    join r in _db.order_requests.AsNoTracking()
                        on q.order_request_id equals r.order_request_id into rj
                    from r in rj.DefaultIfEmpty()

                    where orderIds.Contains(o.order_id)

                    select new CustomerRow
                    {
                        order_id = o.order_id,
                        customer_name = r != null && !string.IsNullOrWhiteSpace(r.customer_name)
                            ? r.customer_name
                            : ""
                    }
                ).ToListAsync(ct);

            var customerByOrderId = customerRows
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().customer_name);

            var ordersById = orderIds.Count == 0
                ? new Dictionary<int, order>()
                : await _db.orders
                    .AsNoTracking()
                    .Where(x => orderIds.Contains(x.order_id))
                    .ToDictionaryAsync(x => x.order_id, ct);

            var groupLinkRows = await (
                from po in _db.prod_orders.AsNoTracking()

                join gp in _db.productions.AsNoTracking()
                    on po.prod_id equals gp.prod_id

                join o in _db.orders.AsNoTracking()
                    on po.order_id equals o.order_id into oj
                from o in oj.DefaultIfEmpty()

                where gp.prod_kind == "GROUP"
                      && (
                            orderIds.Contains(po.order_id)
                            || baseGroupProdIds.Contains(po.prod_id)
                         )

                select new
                {
                    prod_order_id = po.id,

                    group_prod_id = po.prod_id,
                    group_code = gp.code,
                    group_status = gp.status,
                    group_process_codes = gp.group_process_codes,
                    group_total_qty = gp.group_total_qty,
                    group_product_type_id = gp.product_type_id,
                    group_created_at = gp.created_at,
                    group_planned_start_date = gp.planned_start_date,
                    group_actual_start_date = gp.actual_start_date,
                    group_end_date = gp.end_date,

                    po.order_id,
                    order_code = o != null ? o.code : null,
                    po.single_prod_id,
                    po.qty,
                    po.product_type_id,
                    po.product_process,
                    prod_order_status = po.status,
                    prod_order_created_at = po.created_at
                }
            ).ToListAsync(ct);

            var groupProdIds = groupLinkRows
                .Select(x => x.group_prod_id)
                .Concat(baseGroupProdIds)
                .Distinct()
                .ToList();

            var groupSummaries = groupProdIds.Count == 0
                ? new List<GroupSummaryRow>()
                : await (
                    from po in _db.prod_orders.AsNoTracking()

                    join o in _db.orders.AsNoTracking()
                        on po.order_id equals o.order_id

                    where groupProdIds.Contains(po.prod_id)
                          && po.status == "Active"

                    group new { po, o } by po.prod_id into g

                    select new GroupSummaryRow
                    {
                        group_prod_id = g.Key,
                        earliest_delivery_date = g.Min(x => x.o.delivery_date),
                        total_qty = g.Sum(x => x.po.qty)
                    }
                ).ToListAsync(ct);

            var groupSummaryByGroupProdId = groupSummaries
                .GroupBy(x => x.group_prod_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.First());

            var allGroupMembers = groupProdIds.Count == 0
                ? new List<ProdOrderInfoDto>()
                : await (
                    from po in _db.prod_orders.AsNoTracking()

                    join o in _db.orders.AsNoTracking()
                        on po.order_id equals o.order_id into oj
                    from o in oj.DefaultIfEmpty()

                    where groupProdIds.Contains(po.prod_id)

                    orderby po.prod_id, po.order_id

                    select new ProdOrderInfoDto
                    {
                        prod_order_id = po.id,
                        group_prod_id = po.prod_id,
                        order_id = po.order_id,
                        order_code = o != null ? o.code : null,
                        single_prod_id = po.single_prod_id,
                        qty = po.qty,
                        product_type_id = po.product_type_id,
                        product_process = po.product_process,
                        status = po.status,
                        created_at = po.created_at
                    }
                ).ToListAsync(ct);

            var groupMembersByGroupProdId = allGroupMembers
                .GroupBy(x => x.group_prod_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList());

            var groupLinksByOrderId = groupLinkRows
                .GroupBy(x => x.order_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList());

            /*
             * Với logic mới, task ghép đã bị xóa khỏi SINGLE.
             * Phần này vẫn giữ để tương thích dữ liệu cũ còn GroupedWaiting/task_link.
             */
            var linkedSingleTaskIds = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    prodIds.Contains(x.single_prod_id) &&
                    x.single_task_id.HasValue)
                .Select(x => x.single_task_id!.Value)
                .Distinct()
                .ToListAsync(ct);

            var linkedSingleTaskIdSet = linkedSingleTaskIds.ToHashSet();

            var taskRows = await _db.tasks
                .AsNoTracking()
                .Where(t => t.prod_id != null && prodIds.Contains(t.prod_id.Value))
                .Select(t => new TaskRow
                {
                    TaskId = t.task_id,
                    ProdId = t.prod_id!.Value,
                    ProcessId = t.process_id,
                    SeqNum = t.seq_num,
                    Status = t.status,
                    StartTime = t.start_time,
                    EndTime = t.end_time,
                    PlannedStartTime = t.planned_start_time,
                    PlannedEndTime = t.planned_end_time
                })
                .ToListAsync(ct);

            var tasksByProd = taskRows
                .GroupBy(x => x.ProdId)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderBy(x => x.SeqNum ?? int.MaxValue)
                        .ThenBy(x => x.TaskId)
                        .ToList());

            var productTypeIds = baseRows
                .Select(x => x.product_type_id)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToList();

            var stepRows = productTypeIds.Count == 0
                ? new List<StepRow>()
                : await _db.product_type_processes
                    .AsNoTracking()
                    .Where(p =>
                        productTypeIds.Contains(p.product_type_id) &&
                        (p.is_active ?? true))
                    .Select(p => new StepRow
                    {
                        ProductTypeId = p.product_type_id,
                        ProcessId = p.process_id,
                        SeqNum = p.seq_num,
                        ProcessName = p.process_name,
                        ProcessCode = p.process_code
                    })
                    .ToListAsync(ct);

            var stepsByProductType = stepRows
                .GroupBy(x => x.ProductTypeId)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .OrderBy(x => x.SeqNum ?? int.MaxValue)
                        .ThenBy(x => x.ProcessId)
                        .ToList());

            var result = new List<ProducingOrderCardDto>();

            foreach (var r in baseRows)
            {
                var prodKind = (r.prod_kind ?? "").Trim();

                var isGroupRow = string.Equals(
                    prodKind,
                    "GROUP",
                    StringComparison.OrdinalIgnoreCase);

                var isSplitRow = string.Equals(
                    prodKind,
                    "SPLIT",
                    StringComparison.OrdinalIgnoreCase);

                var isSingleRow = !isGroupRow && !isSplitRow;

                tasksByProd.TryGetValue(r.prod_id, out var allTasksOfProd);
                allTasksOfProd ??= new List<TaskRow>();

                var tasksForCard = ResolveTasksForProductionCard(
                    allTasksOfProd,
                    isSingleRow,
                    linkedSingleTaskIdSet);

                var ptId = r.product_type_id ?? 0;

                stepsByProductType.TryGetValue(ptId, out var allStepsOfProductType);
                allStepsOfProductType ??= new List<StepRow>();

                var routeProcessCsv =
                    isGroupRow || isSplitRow
                        ? r.group_process_codes
                        : r.first_item_production_process;

                var orderedAllSteps = allStepsOfProductType
                    .OrderBy(s => s.SeqNum ?? int.MaxValue)
                    .ThenBy(s => s.ProcessId)
                    .ToList();

                var routeSteps = string.IsNullOrWhiteSpace(routeProcessCsv)
                    ? orderedAllSteps
                    : ResolveFixedRoute(
                        orderedAllSteps,
                        x => x.ProcessCode,
                        routeProcessCsv);

                if (routeSteps.Count == 0 && tasksForCard.Count > 0)
                    routeSteps = orderedAllSteps;

                var stepsForCard = ResolveStepsForProductionCard(
                    routeSteps,
                    tasksForCard,
                    isSingleRow);

                var visibleSteps = stepsForCard
                    .OrderBy(x => x.SeqNum ?? int.MaxValue)
                    .ThenBy(x => x.ProcessId)
                    .ToList();

                if (visibleSteps.Count == 0)
                    visibleSteps = stepsForCard;

                var visibleTasks = tasksForCard
                    .Where(t =>
                        visibleSteps.Any(s =>
                            (t.ProcessId.HasValue && t.ProcessId.Value == s.ProcessId) ||
                            (
                                t.SeqNum.HasValue &&
                                s.SeqNum.HasValue &&
                                t.SeqNum.Value == s.SeqNum.Value
                            )))
                    .OrderBy(t => t.SeqNum ?? int.MaxValue)
                    .ThenBy(t => t.TaskId)
                    .ToList();

                var stageStatuses = visibleSteps
                    .Select(step =>
                    {
                        var task = visibleTasks.FirstOrDefault(t =>
                                       t.ProcessId.HasValue &&
                                       t.ProcessId.Value == step.ProcessId)
                                   ?? visibleTasks.FirstOrDefault(t =>
                                       t.SeqNum.HasValue &&
                                       step.SeqNum.HasValue &&
                                       t.SeqNum.Value == step.SeqNum.Value);

                        return new ProductionStageStatusDto
                        {
                            task_id = task?.TaskId,
                            process_id = step.ProcessId,
                            seq_num = step.SeqNum,
                            process_code = step.ProcessCode,
                            process_name = step.ProcessName,
                            status = ResolveTaskStageStatus(task),
                            start_time = task?.StartTime,
                            end_time = task?.EndTime,
                            planned_start_time = task?.PlannedStartTime,
                            planned_end_time = task?.PlannedEndTime,
                            is_current = false
                        };
                    })
                    .OrderBy(x => x.seq_num ?? int.MaxValue)
                    .ToList();

                var currentTask = ResolveCurrentTaskForCard(visibleTasks);

                string? currentStage = null;
                string? currentStageStatus = null;

                if (currentTask != null)
                {
                    var currentStageItem = stageStatuses.FirstOrDefault(x =>
                        x.task_id == currentTask.TaskId);

                    if (currentStageItem == null && currentTask.ProcessId.HasValue)
                    {
                        currentStageItem = stageStatuses.FirstOrDefault(x =>
                            x.process_id == currentTask.ProcessId.Value);
                    }

                    if (currentStageItem != null)
                    {
                        currentStage = currentStageItem.process_name;
                        currentStageStatus = currentStageItem.status;
                        currentStageItem.is_current = true;
                    }
                }

                if (currentStage == null)
                {
                    var firstStage = stageStatuses
                        .OrderBy(x => x.seq_num ?? int.MaxValue)
                        .FirstOrDefault();

                    if (firstStage != null)
                    {
                        currentStage = firstStage.process_name;
                        currentStageStatus = firstStage.status;
                        firstStage.is_current = true;
                    }
                }

                var stages = visibleSteps
                    .OrderBy(s => s.SeqNum ?? int.MaxValue)
                    .ThenBy(s => s.ProcessId)
                    .Select(s => s.ProcessName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                List<ProductionGroupInfoDto> groupInfos;

                if (isGroupRow)
                {
                    groupMembersByGroupProdId.TryGetValue(r.prod_id, out var members);
                    members ??= new List<ProdOrderInfoDto>();

                    var isActiveGroup =
                        !string.Equals(r.production_status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(r.production_status, "Completed", StringComparison.OrdinalIgnoreCase);

                    groupInfos = new List<ProductionGroupInfoDto>
            {
                new ProductionGroupInfoDto
                {
                    group_id = r.prod_id,
                    group_prod_id = r.prod_id,
                    group_code = r.production_code,
                    group_status = r.production_status,
                    group_process_codes = r.group_process_codes,
                    group_total_qty = r.group_total_qty,
                    product_type_id = r.product_type_id,

                    group_created_at = r.created_at,
                    group_planned_start_date = r.planned_start_date,
                    group_actual_start_date = r.actual_start_date,
                    group_end_date = r.end_date,

                    is_active_group = isActiveGroup,
                    current_prod_order = null,
                    prod_orders = members
                }
            };
                }
                else
                {
                    var currentOrderId = r.order_id ?? 0;

                    groupLinksByOrderId.TryGetValue(currentOrderId, out var orderGroupLinks);
                    orderGroupLinks ??= new();

                    groupInfos = orderGroupLinks
                        .GroupBy(x => x.group_prod_id)
                        .Select(g =>
                        {
                            var first = g.First();

                            groupMembersByGroupProdId.TryGetValue(first.group_prod_id, out var members);
                            members ??= new List<ProdOrderInfoDto>();

                            var currentProdOrder = members.FirstOrDefault(x =>
                                x.order_id == currentOrderId);

                            var isActiveGroup =
                                string.Equals(currentProdOrder?.status, "Active", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(first.group_status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(first.group_status, "Completed", StringComparison.OrdinalIgnoreCase);

                            return new ProductionGroupInfoDto
                            {
                                group_id = first.group_prod_id,
                                group_prod_id = first.group_prod_id,
                                group_code = first.group_code,
                                group_status = first.group_status,
                                group_process_codes = first.group_process_codes,
                                group_total_qty = first.group_total_qty,
                                product_type_id = first.group_product_type_id,

                                group_created_at = first.group_created_at,
                                group_planned_start_date = first.group_planned_start_date,
                                group_actual_start_date = first.group_actual_start_date,
                                group_end_date = first.group_end_date,

                                is_active_group = isActiveGroup,
                                current_prod_order = currentProdOrder,
                                prod_orders = members
                            };
                        })
                        .OrderByDescending(x => x.is_active_group)
                        .ThenByDescending(x => x.group_prod_id)
                        .ToList();
                }

                var activeGroup = groupInfos.FirstOrDefault(x => x.is_active_group);

                var progress = ComputeProgressByTaskStatus(
                    visibleSteps.Count,
                    visibleTasks);

                order? ord = null;

                if (r.order_id.HasValue)
                    ordersById.TryGetValue(r.order_id.Value, out ord);

                var customerName = r.customer_name ?? "";

                if (!isGroupRow && r.order_id.HasValue)
                {
                    if (customerByOrderId.TryGetValue(r.order_id.Value, out var loadedCustomerName) &&
                        !string.IsNullOrWhiteSpace(loadedCustomerName))
                    {
                        customerName = loadedCustomerName;
                    }
                }

                if (isGroupRow)
                    customerName = "Production ghép";

                groupSummaryByGroupProdId.TryGetValue(r.prod_id, out var groupSummary);

                var deliveryDate = isGroupRow
                    ? groupSummary?.earliest_delivery_date
                    : r.delivery_date;

                var quantity =
                    isGroupRow
                        ? r.group_total_qty > 0
                            ? r.group_total_qty
                            : groupSummary?.total_qty ?? 0
                        : isSplitRow
                            ? r.group_total_qty > 0
                                ? r.group_total_qty
                                : r.first_item_quantity ?? 0
                            : r.first_item_quantity ?? 0;

                var productName = isGroupRow
                    ? "Lệnh sản xuất ghép"
                    : !string.IsNullOrWhiteSpace(r.first_item_product_name)
                        ? r.first_item_product_name
                        : r.production_code ?? r.code;

                var displayStatus = r.production_status;

                var isProductionReady = isGroupRow
                    ? groupInfos
                        .SelectMany(x => x.prod_orders)
                        .Any() &&
                      groupInfos
                        .SelectMany(x => x.prod_orders)
                        .All(x => string.Equals(x.status, "Active", StringComparison.OrdinalIgnoreCase))
                    : ord?.is_production_ready;

                var currentTaskForCanStart = ResolveCurrentTaskForCanStart(visibleTasks);

                var canStartResult = await ResolveCanStartForProductionCardAsync(
                    r,
                    currentTaskForCanStart,
                    isProductionReady,
                    ct);

                var canStart = canStartResult.can_start;
                var canStartMessage = canStartResult.message;
                List<int>? listOrderId = null;

                if (isGroupRow)
                {
                    groupMembersByGroupProdId.TryGetValue(
                        r.prod_id,
                        out var membersForListOrderId);

                    membersForListOrderId ??= new List<ProdOrderInfoDto>();

                    listOrderId = membersForListOrderId
                        .Where(x => x.order_id > 0)
                        .Where(x =>
                            string.IsNullOrWhiteSpace(x.status) ||
                            !string.Equals(x.status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.order_id)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList();
                }

                result.Add(new ProducingOrderCardDto
                {
                    prod_id = r.prod_id,
                    production_id = r.prod_id,
                    list_order_id = listOrderId,
                    order_id = r.order_id,
                    code = r.code,

                    customer_name = customerName,
                    product_name = productName,
                    quantity = quantity,

                    delivery_date = deliveryDate,

                    progress_percent = progress,
                    current_stage = currentStage,
                    planned_start_date = r.planned_start_date,
                    planned_end_date = r.planned_end_date,
                    actual_start_date = r.actual_start_date,
                    end_date = r.end_date,
                    status = displayStatus,
                    production_status = r.production_status,
                    order_status = r.order_status,
                    stage_status = currentStageStatus,
                    sub_product_issue_file = r.sub_product_issue_file,
                    created_at = r.created_at,
                    start_date = r.actual_start_date,

                    prod_kind = r.prod_kind,
                    production_code = r.production_code,

                    is_group_production = isGroupRow,
                    is_split_production = isSplitRow,

                    is_production_ready = isProductionReady,
                    production_approval_flow = r.production_approval_flow,
                    is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(r.production_approval_flow),
                    production_approval_label = ProductionApprovalFlowHelper.Label(r.production_approval_flow),
                    production_method = r.production_method,
                    is_full_process = r.is_full_process,
                    sub_product_id = r.sub_product_id,
                    sub_product_used_qty = r.sub_product_used_qty,
                    nvl_qty = r.nvl_qty,

                    gm_note = r.gm_note,
                    mgr_note = r.mgr_note,

                    group_status = isGroupRow || isSplitRow
                        ? r.production_status
                        : activeGroup?.group_status,

                    group_process_codes = isGroupRow || isSplitRow
                        ? r.group_process_codes
                        : activeGroup?.group_process_codes,

                    group_total_qty = isGroupRow || isSplitRow
                        ? quantity
                        : activeGroup?.group_total_qty,
                    can_start = canStart,
                    can_start_message = canStartMessage,

                    stages = stages,
                    stage_statuses = stageStatuses
                });
            }

            return new PagedResultLite<ProducingOrderCardDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = result
            };
        }

        private static List<TaskRow> ResolveTasksForProductionCard(
    List<TaskRow> allTasksOfProd,
    bool isSingleRow,
    HashSet<int> linkedSingleTaskIds)
        {
            if (allTasksOfProd == null || allTasksOfProd.Count == 0)
                return new List<TaskRow>();

            if (!isSingleRow)
            {
                return allTasksOfProd
                    .OrderBy(x => x.SeqNum ?? int.MaxValue)
                    .ThenBy(x => x.TaskId)
                    .ToList();
            }

            return allTasksOfProd
                .Where(x => !linkedSingleTaskIds.Contains(x.TaskId))
                .Where(x => !string.Equals(x.Status, "GroupedWaiting", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.SeqNum ?? int.MaxValue)
                .ThenBy(x => x.TaskId)
                .ToList();
        }

        private static TaskRow? ResolveCurrentTaskForCard(List<TaskRow> tasks)
        {
            if (tasks == null || tasks.Count == 0)
                return null;

            var inProcessing = tasks
                .OrderBy(x => x.SeqNum ?? int.MaxValue)
                .ThenBy(x => x.TaskId)
                .FirstOrDefault(x =>
                    x.StartTime != null &&
                    x.EndTime == null);

            if (inProcessing != null)
                return inProcessing;

            var nextUnfinished = tasks
                .OrderBy(x => x.SeqNum ?? int.MaxValue)
                .ThenBy(x => x.TaskId)
                .FirstOrDefault(x =>
                    x.EndTime == null &&
                    !string.Equals(x.Status, "Finished", StringComparison.OrdinalIgnoreCase));

            if (nextUnfinished != null)
                return nextUnfinished;

            return tasks
                .OrderByDescending(x => x.SeqNum ?? int.MinValue)
                .ThenByDescending(x => x.TaskId)
                .FirstOrDefault();
        }

        private static List<StepRow> ResolveStepsForProductionCard(
    List<StepRow> routeSteps,
    List<TaskRow> tasksForCard,
    bool isSingleRow)
        {
            if (routeSteps == null || routeSteps.Count == 0)
                return new List<StepRow>();

            if (tasksForCard == null || tasksForCard.Count == 0)
            {
                return routeSteps
                    .OrderBy(x => x.SeqNum ?? int.MaxValue)
                    .ThenBy(x => x.ProcessId)
                    .ToList();
            }

            var processIds = tasksForCard
                .Where(x => x.ProcessId.HasValue)
                .Select(x => x.ProcessId!.Value)
                .Distinct()
                .ToHashSet();

            var seqNums = tasksForCard
                .Where(x => x.SeqNum.HasValue)
                .Select(x => x.SeqNum!.Value)
                .Distinct()
                .ToHashSet();

            var filtered = routeSteps
                .Where(x =>
                    processIds.Contains(x.ProcessId) ||
                    (
                        x.SeqNum.HasValue &&
                        seqNums.Contains(x.SeqNum.Value)
                    ))
                .OrderBy(x => x.SeqNum ?? int.MaxValue)
                .ThenBy(x => x.ProcessId)
                .ToList();

            if (filtered.Count > 0)
                return filtered;

            /*
             * Fallback cuối cùng:
             * - Nếu routeSteps đã được ResolveFixedRoute đúng thì trả routeSteps.
             * - Không trả all route khi SINGLE đã bị xóa task ghép.
             */
            return routeSteps
                .OrderBy(x => x.SeqNum ?? int.MaxValue)
                .ThenBy(x => x.ProcessId)
                .ToList();
        }

        private static decimal ComputeProgressByTaskStatus(
      int totalVisibleSteps,
      List<TaskRow> visibleTasks)
        {
            if (totalVisibleSteps <= 0)
                return 0m;

            if (visibleTasks == null || visibleTasks.Count == 0)
                return 0m;

            var finished = visibleTasks.Count(x =>
                x.EndTime != null ||
                string.Equals(x.Status, "Finished", StringComparison.OrdinalIgnoreCase));

            var percent = finished * 100m / totalVisibleSteps;

            return Math.Round(percent, 1);
        }

        private sealed class CustomerRow
        {
            public int order_id { get; set; }
            public string customer_name { get; set; } = "";
        }

        private sealed class GroupSummaryRow
        {
            public int group_prod_id { get; set; }

            public DateTime? earliest_delivery_date { get; set; }

            public int total_qty { get; set; }
        }

        public async Task<ProductionProgressResponse> GetProgressAsync(int prodId)
        {
            var tasks = await _db.tasks
                .AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.task_id, t.status })
                .ToListAsync();

            var total = tasks.Count;
            if (total <= 0)
                return new ProductionProgressResponse
                {
                    prod_id = prodId,
                    total_steps = 0,
                    finished_steps = 0,
                    progress_percent = 0
                };

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var finishedTaskIds = await _db.task_logs
                .AsNoTracking()
                .Where(l => l.task_id != null
                    && taskIds.Contains(l.task_id.Value)
                    && (l.action_type == "Finished" || l.action_type == "FinishedByGroup"))
                .Select(l => l.task_id!.Value)
                .Distinct()
                .ToListAsync();

            var finished = tasks.Count(t =>
                string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase)
                && finishedTaskIds.Contains(t.task_id));

            var percent = Math.Round((finished * 100m) / total, 1);

            return new ProductionProgressResponse
            {
                prod_id = prodId,
                total_steps = total,
                finished_steps = finished,
                progress_percent = percent
            };
        }

        public async Task<ProductionDetailDto?> GetProductionDetailByProdIdAsync(
    int prodId,
    CancellationToken ct = default)
        {
            var header = await (
                from pr in _db.productions.AsNoTracking()

                join o in _db.orders.AsNoTracking()
                    on pr.order_id equals o.order_id into oj
                from o in oj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking()
                    on pr.order_id equals (int?)r.order_id into rj
                from r in rj.DefaultIfEmpty()

                join pt in _db.product_types.AsNoTracking()
                    on pr.product_type_id equals pt.product_type_id into ptj
                from pt in ptj.DefaultIfEmpty()

                join sp in _db.sub_products.AsNoTracking()
                    on pr.sub_product_id equals sp.id into spj
                from sp in spj.DefaultIfEmpty()

                where pr.prod_id == prodId

                select new
                {
                    pr,
                    sp,
                    o,

                    product_type_name = pt != null ? pt.name : null,
                    packaging_standard = pt != null ? pt.packaging_standard : null,

                    customer_name =
                        pr.order_id == null
                            ? "Production ghép"
                            : r != null && r.customer_name != null && r.customer_name != ""
                                ? r.customer_name
                                : "Khách hàng",

                    first_item = pr.order_id == null ? null : _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == pr.order_id.Value)
                        .OrderBy(i => i.item_id)
                        .Select(i => new ProductionDetailSourceItem
                        {
                            item_id = i.item_id,
                            product_name = i.product_name,
                            quantity = i.quantity,
                            production_process = i.production_process,
                            length_mm = EF.Property<int?>(i, "length_mm"),
                            width_mm = EF.Property<int?>(i, "width_mm"),
                            height_mm = EF.Property<int?>(i, "height_mm"),
                            est_ink_weight_kg = i.est_ink_weight_kg
                        })
                        .FirstOrDefault()
                }
            ).FirstOrDefaultAsync(ct);

            if (header == null)
                return null;

            var isGroupProduction = string.Equals(
                header.pr.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

            var isSplitProduction = string.Equals(
                header.pr.prod_kind,
                "SPLIT",
                StringComparison.OrdinalIgnoreCase);

            var routeProcessCsv = !string.IsNullOrWhiteSpace(header.pr.group_process_codes)
                ? header.pr.group_process_codes
                : header.first_item?.production_process;

            var dto = new ProductionDetailDto
            {
                prod_id = header.pr.prod_id,
                import_recieve_path = header.pr.import_recieve_path,

                production_code = header.pr.code,
                production_status = header.pr.status,

                created_at = header.pr.created_at,
                planned_start_date = header.pr.planned_start_date,
                actual_start_date = header.pr.actual_start_date,
                start_date = header.pr.actual_start_date,
                end_date = header.pr.end_date,
                planned_end_date = header.pr.planned_end_date,
                order_id = header.o?.order_id,
                order_code = header.o?.code,
                delivery_date = header.o?.delivery_date,
                customer_name = header.customer_name ?? "Khách ẩn tên",
                product_name = isGroupProduction
        ? "Lệnh sản xuất ghép"
        : header.first_item?.product_name,

                quantity = isGroupProduction
        ? header.pr.group_total_qty
        : header.first_item?.quantity ?? header.pr.group_total_qty,

                product_type_id = header.pr.product_type_id,
                packaging_standard = header.packaging_standard,

                length_mm = header.first_item?.length_mm,
                width_mm = header.first_item?.width_mm,
                height_mm = header.first_item?.height_mm,

                production_method = header.pr.prod_method,
                sub_product_id = header.pr.sub_product_id,
                sub_product_used_qty = header.pr.sub_product_used_qty,
                nvl_qty = header.pr.nvl_qty,
                sub_product_process = header.sp != null ? header.sp.product_process : null,
                sub_product_issue_file = header.pr.sub_product_issue_file,
                is_full_process = header.pr.is_full_process,

                production_approval_flow = header.pr.production_approval_flow,
                is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(header.pr.production_approval_flow),
                production_approval_label = ProductionApprovalFlowHelper.Label(header.pr.production_approval_flow)
            };

            order_request? orderReq = null;
            cost_estimate? estimate = null;

            int sheetsRequired = 0;
            int sheetsWaste = 0;
            int sheetsTotal = 0;
            int nUp = 1;

            ProductionDetailSourceItem? sourceItem = header.first_item;

            int? materialSourceOrderId = dto.order_id;

            if (isGroupProduction)
            {
                materialSourceOrderId = await ResolveGroupRepresentativeOrderIdAsync(
                    header.pr.prod_id,
                    ct);

                if (materialSourceOrderId.HasValue)
                {
                    sourceItem = await LoadProductionDetailSourceItemAsync(
                        materialSourceOrderId.Value,
                        ct);
                }
            }

            if (materialSourceOrderId.HasValue)
            {
                orderReq = await _db.order_requests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == materialSourceOrderId.Value, ct);

                if (orderReq != null)
                {
                    if (orderReq.accepted_estimate_id.HasValue &&
                        orderReq.accepted_estimate_id.Value > 0)
                    {
                        estimate = await _db.cost_estimates
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                x => x.estimate_id == orderReq.accepted_estimate_id.Value
                                  && x.order_request_id == orderReq.order_request_id,
                                ct);
                    }

                    estimate ??= await _db.cost_estimates
                        .AsNoTracking()
                        .Where(x => x.order_request_id == orderReq.order_request_id)
                        .OrderByDescending(x => x.is_active)
                        .ThenByDescending(x => x.estimate_id)
                        .FirstOrDefaultAsync(ct);

                    if (estimate != null)
                    {
                        sheetsRequired = estimate.sheets_required;
                        sheetsWaste = estimate.sheets_waste;
                        sheetsTotal = estimate.sheets_total;
                        nUp = estimate.n_up > 0 ? estimate.n_up : 1;
                    }
                }
            }

            if (isGroupProduction && header.pr.group_total_qty > 0)
            {
                sheetsRequired = header.pr.group_total_qty;
                sheetsWaste = 0;
                sheetsTotal = header.pr.group_total_qty;
                nUp = 1;
            }

            dto.n_up = nUp;
            dto.ready_print_file = orderReq?.print_ready_file;
            dto.ink_type_names = estimate?.ink_type_names;
            dto.wave_type = estimate?.wave_type;
            dto.paper_name = estimate?.paper_name;
            dto.coating_type = estimate?.coating_type;
            dto.paper_alternative = estimate?.paper_alternative;
            dto.wave_alternative = estimate?.wave_alternative;
            dto.lamination_material_id = estimate?.lamination_material_id;
            dto.lamination_material_code = estimate?.lamination_material_code;
            dto.lamination_material_name = estimate?.lamination_material_name;

            decimal estInkWeightKg = 0m;

            if (sourceItem?.est_ink_weight_kg.HasValue == true)
                estInkWeightKg = sourceItem.est_ink_weight_kg.Value;

            int? numberOfPlates = orderReq?.number_of_plates;
            decimal estCoatingGlueWeightKg = estimate?.coating_glue_weight_kg ?? 0m;

            material? coatingMaterial = null;

            if (estimate != null &&
                !IsNoCoatingType(estimate.coating_type))
            {
                coatingMaterial = await ResolveCoatingMaterialForDetailAsync(estimate, ct);
            }

            var coatingMaterialCode = coatingMaterial?.code
                ?? ResolveCoatingMaterialCodeForDetail(estimate?.coating_type);

            var coatingMaterialName = coatingMaterial?.name
                ?? (!IsNoCoatingType(estimate?.coating_type)
                    ? ProductionFlowHelper.ResolveCoatingDisplayName(estimate?.coating_type)
                    : null);

            var coatingMaterialUnit = coatingMaterial?.unit ?? "kg";

            var currentProdId = header.pr.prod_id;

            var tasks = await _db.tasks.AsNoTracking()
                .Where(t => t.prod_id == currentProdId)
                .Select(t => new
                {
                    t.task_id,
                    t.prod_id,
                    t.seq_num,
                    t.name,
                    t.status,
                    t.machine,
                    t.start_time,
                    t.end_time,
                    t.planned_start_time,
                    t.planned_end_time,
                    t.process_id,
                    t.is_taken_sub_product
                })
                .ToListAsync(ct);

            var lastTask = tasks
    .OrderByDescending(t => t.seq_num ?? 0)
    .ThenByDescending(t => t.task_id)
    .FirstOrDefault();

            var allTasksFinished = tasks.Count > 0 &&
                tasks.All(t =>
                    string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    t.end_time != null);

            dto.all_tasks_finished = allTasksFinished;
            dto.waiting_manual_importing = allTasksFinished && !string.Equals(header.pr.status, "Importing");

            var taskIds = tasks
                .Select(x => x.task_id)
                .ToList();

            var logs = await _db.task_logs.AsNoTracking()
                .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                .OrderBy(l => l.log_time)
                .Select(l => new TaskLogDto
                {
                    log_id = l.log_id,
                    task_id = l.task_id!.Value,
                    action_type = l.action_type,
                    qty_good = l.qty_good ?? 0,
                    log_time = l.log_time,
                    scanned_code = l.scanned_code,
                    scanned_by_user_id = l.scanned_by_user_id,

                    reason = l.reason,
                    comment = l.reason,

                    report_image_url = l.report_image_url,

                    material_usage_json = l.material_usage_json,
                    reference_input_json = l.reference_input_json,
                    output_json = l.output_json
                })
                .ToListAsync(ct);

            foreach (var log in logs)
            {
                log.report_image_urls = SplitImageUrls(log.report_image_url);

                if (!string.IsNullOrWhiteSpace(log.material_usage_json))
                {
                    try
                    {
                        log.material_usages = JsonSerializer.Deserialize<List<TaskMaterialUsageLogItemDto>>(
                            log.material_usage_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            }) ?? new List<TaskMaterialUsageLogItemDto>();
                    }
                    catch
                    {
                        log.material_usages = new List<TaskMaterialUsageLogItemDto>();
                    }
                }
                else
                {
                    log.material_usages = new List<TaskMaterialUsageLogItemDto>();
                }

                if (!string.IsNullOrWhiteSpace(log.reference_input_json))
                {
                    try
                    {
                        log.reference_inputs = JsonSerializer.Deserialize<List<TaskReferenceUsageInputDto>>(
                            log.reference_input_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            }) ?? new List<TaskReferenceUsageInputDto>();
                    }
                    catch
                    {
                        log.reference_inputs = new List<TaskReferenceUsageInputDto>();
                    }
                }
                else
                {
                    log.reference_inputs = new List<TaskReferenceUsageInputDto>();
                }

                if (!string.IsNullOrWhiteSpace(log.output_json))
                {
                    try
                    {
                        log.outputs = JsonSerializer.Deserialize<List<TaskOutputReportDto>>(
                            log.output_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            }) ?? new List<TaskOutputReportDto>();
                    }
                    catch
                    {
                        log.outputs = new List<TaskOutputReportDto>();
                    }
                }
                else
                {
                    log.outputs = new List<TaskOutputReportDto>();
                }
            }

            var ptId = header.pr.product_type_id;
            List<ProductTypeProcessStepDto> steps = new();

            if (ptId.HasValue)
            {
                steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .OrderBy(p => p.seq_num)
                    .Select(p => new ProductTypeProcessStepDto
                    {
                        process_id = p.process_id,
                        seq_num = p.seq_num,
                        process_name = p.process_name,
                        process_code = EF.Property<string?>(p, "process_code"),
                        machine = p.machine
                    })
                    .ToListAsync(ct);
            }

            steps = ResolveFixedRoute(
                steps.OrderBy(x => x.seq_num).ToList(),
                x => x.process_code,
                routeProcessCsv
            );

            var stages = new List<ProductionStageDto>();
            StageOutputRef? prevOutput = null;
            var routeCodes = steps.Select(x => x.process_code).ToList();

            for (var stageIndex = 0; stageIndex < steps.Count; stageIndex++)
            {
                var s = steps[stageIndex];
                var pcode = (s.process_code ?? "").Trim().ToUpperInvariant();

                var task = tasks.FirstOrDefault(t => t.process_id == s.process_id)
                           ?? tasks.FirstOrDefault(t => (t.seq_num ?? -1) == s.seq_num);

                var stageLogs = task == null
                    ? new List<TaskLogDto>()
                    : LogsByTaskId(logs, task.task_id);

                var qtyGoodFromLog = ResolveActualOutputQtyFromTaskLogs(stageLogs);
                var qtyBadFromLog = ResolveActualBadQtyFromOutputJson(stageLogs);

                var denom = qtyGoodFromLog + qtyBadFromLog;

                var wastePct = denom <= 0
                    ? 0m
                    : Math.Round((qtyBadFromLog * 100m) / denom, 2);

                /*
                 * Không ép GROUP = 0 ở đây nữa.
                 * Nếu là GROUP mà estimate của representative không đủ đúng,
                 * phía dưới ApplyTaskLogJsonToProductionStage sẽ lấy lại số đúng từ log JSON.
                 */
                var stageCoatingGlueWeightKg = estCoatingGlueWeightKg;
                var stageLaminationWeightKg = estimate?.lamination_weight_kg ?? 0m;

                var io = BuildStageIO(
                    processCode: pcode,
                    processName: s.process_name ?? "",
                    detail: dto,
                    prevOutput: prevOutput,
                    sheetsRequired: sheetsRequired,
                    sheetsWaste: sheetsWaste,
                    sheetsTotal: sheetsTotal,
                    nUp: nUp,
                    qtyGood: qtyGoodFromLog,
                    numberOfPlates: numberOfPlates,
                    estInkWeightKg: estInkWeightKg,
                    currentStageIndex: stageIndex,
                    routeProcessCodes: routeCodes,
                    paperCode: estimate?.paper_code,
                    paperName: estimate?.paper_name,
                    waveType: estimate?.wave_type,
                    coatingType: estimate?.coating_type,
                    coatingMaterialCode: coatingMaterialCode,
                    coatingMaterialName: coatingMaterialName,
                    coatingMaterialUnit: coatingMaterialUnit,
                    estCoatingGlueWeightKg: stageCoatingGlueWeightKg,
                    inkTypeNames: estimate?.ink_type_names,
                    laminationMaterialCode: estimate?.lamination_material_code,
                    laminationMaterialName: estimate?.lamination_material_name,
                    estLaminationWeightKg: stageLaminationWeightKg
                );

                var stage = new ProductionStageDto
                {
                    process_id = s.process_id,
                    seq_num = s.seq_num,
                    process_name = s.process_name ?? "",
                    process_code = s.process_code,

                    machine = task?.machine ?? s.machine,

                    task_id = task?.task_id,
                    task_name = task?.name,
                    status = task?.status,
                    start_time = task?.start_time,
                    end_time = task?.end_time,

                    qty_good = qtyGoodFromLog,
                    waste_percent = wastePct,

                    last_scan_time = stageLogs.Count == 0
                        ? null
                        : stageLogs.Max(x => x.log_time),

                    logs = stageLogs,

                    input_materials = io.inputs,
                    output_product = io.output,

                    planned_start_time = task?.planned_start_time,
                    planned_end_time = task?.planned_end_time,

                    estimated_output_quantity = io.output.estimated_quantity,
                    actual_output_quantity = io.output.actual_quantity,

                    n_up = nUp,
                    is_taken_sub_product = task?.is_taken_sub_product ?? false
                };

                ApplyTaskLogJsonToProductionStage(stage, stageLogs);
                ApplyOutputActualFallbackFromQtyGood(stage);

                await ApplySubFirstDownstreamActualEstimateAsync(
                    header.pr,
                    stage,
                    steps,
                    ct);

                NormalizeNullActualInputMaterials(stage);

                ApplyCatInputActualEqualsEstimatedForProductionDetail(stage);

                ApplyPreviousStageActualToInputMaterials(
                    stage,
                    prevOutput);

                ApplyPlateActualFromRaloToInStage(
                    stage,
                    stages);

                SyncActualQuantityFieldsForProductionDetail(stage);

                stages.Add(stage);

                prevOutput = new StageOutputRef
                {
                    Name = stage.output_product?.name ?? io.nextOutput.Name,
                    Code = stage.output_product?.code ?? io.nextOutput.Code,
                    Unit = stage.output_product?.unit ?? io.nextOutput.Unit,

                    EstimatedQuantity =
        stage.output_product != null && stage.output_product.estimated_quantity > 0
            ? stage.output_product.estimated_quantity
            : io.nextOutput.EstimatedQuantity,

                    ActualQuantity =
        stage.output_product != null && stage.output_product.actual_quantity > 0
            ? stage.output_product.actual_quantity
            : stage.qty_good > 0
                ? stage.qty_good
                : io.nextOutput.ActualQuantity
                };
            }

            ApplyGroupSubDisplayQuantityWithWasteToHeader(
    dto,
    header.pr,
    stages);

            dto.stages = stages;

            dto.all_tasks_finished = stages.Count > 0 &&
                stages.All(x => string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase));

            dto.waiting_manual_importing =
                string.Equals(dto.production_status, "Importing", StringComparison.OrdinalIgnoreCase);

            return dto;
        }

        private static void ApplyCatInputActualEqualsEstimatedForProductionDetail(
    ProductionStageDto stage)
        {
            if (stage == null)
                return;

            var processCode = NormDetailProcessCode(stage.process_code);

            if (!string.Equals(processCode, "CAT", StringComparison.OrdinalIgnoreCase))
                return;

            if (stage.input_materials == null || stage.input_materials.Count == 0)
                return;

            foreach (var input in stage.input_materials)
            {
                if (input == null)
                    continue;

                if (input.estimated_quantity <= 0)
                    continue;

                input.actual_quantity = input.estimated_quantity;
                input.quantity_source = "Estimated";
            }
        }

        private static bool IsGroupSubProductionForDetail(production prod)
        {
            return prod != null &&
                   string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(prod.prod_method, "SUB", StringComparison.OrdinalIgnoreCase);
        }

        private static int CeilPositiveIntForDetail(decimal value, int fallback)
        {
            if (value <= 0)
                return fallback > 0 ? fallback : 1;

            return Math.Max(1, (int)Math.Ceiling(value));
        }

        private static bool IsPlannedSubInputWithWaste(StageMaterialDto input)
        {
            if (input == null)
                return false;

            return string.Equals(
                input.quantity_source,
                "PlannedSubInputWithWaste",
                StringComparison.OrdinalIgnoreCase);
        }

        private static decimal ResolveGroupSubQuantityWithWasteFromStages(
            production prod,
            List<ProductionStageDto> stages)
        {
            var baseQty = prod?.group_total_qty > 0
                ? prod.group_total_qty
                : 0;

            if (stages == null || stages.Count == 0)
                return baseQty;

            var candidates = new List<decimal>();

            foreach (var stage in stages)
            {
                if (stage == null)
                    continue;

                /*
                 * Sau khi sửa ApplySubFirstDownstreamActualEstimateAsync,
                 * PHU/CAN sẽ có estimated_output_quantity = 6290.
                 */
                if (stage.estimated_output_quantity > 0)
                    candidates.Add(stage.estimated_output_quantity);

                if (stage.output_product != null)
                {
                    if (stage.output_product.estimated_quantity > 0)
                        candidates.Add(stage.output_product.estimated_quantity);

                    if (stage.output_product.quantity > 0)
                        candidates.Add(stage.output_product.quantity);
                }

                /*
                 * Input chính của PHU/CAN đã được set:
                 * quantity_source = PlannedSubInputWithWaste
                 * quantity/estimated_quantity/actual_quantity = 6290
                 */
                if (stage.input_materials != null && stage.input_materials.Count > 0)
                {
                    foreach (var input in stage.input_materials)
                    {
                        if (!IsPlannedSubInputWithWaste(input))
                            continue;

                        if (input.estimated_quantity > 0)
                            candidates.Add(input.estimated_quantity);

                        if (input.quantity > 0)
                            candidates.Add(input.quantity);

                        if (input.actual_quantity > 0)
                            candidates.Add((decimal)input.actual_quantity);
                    }
                }
            }

            var maxCandidate = candidates
                .Where(x => x > 0)
                .DefaultIfEmpty(baseQty)
                .Max();

            /*
             * Không bao giờ để quantity nhỏ hơn group_total_qty.
             * Ví dụ:
             * group_total_qty = 6000
             * planned with waste = 6290
             * => return 6290
             */
            return Math.Max(baseQty, maxCandidate);
        }

        private static void ApplyGroupSubDisplayQuantityWithWasteToHeader(
            ProductionDetailDto dto,
            production prod,
            List<ProductionStageDto> stages)
        {
            if (dto == null || prod == null)
                return;

            if (!IsGroupSubProductionForDetail(prod))
                return;

            var baseQty = prod.group_total_qty > 0
                ? prod.group_total_qty
                : dto.quantity;

            var quantityWithWaste = ResolveGroupSubQuantityWithWasteFromStages(
                prod,
                stages);

            if (quantityWithWaste <= 0)
                return;

            /*
             * ProductionDetailDto.quantity của bạn đang trả int trong JSON.
             * Nên dùng Ceiling để tránh mất phần lẻ nếu sau này số có decimal.
             */
            dto.quantity = CeilPositiveIntForDetail(
                quantityWithWaste,
                baseQty);
        }

        private static readonly JsonSerializerOptions DetailJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static List<T> ParseJsonArraySafe<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<T>();

            try
            {
                return JsonSerializer.Deserialize<List<T>>(json, DetailJsonOptions)
                       ?? new List<T>();
            }
            catch
            {
                return new List<T>();
            }
        }

        private static string NormDetailCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static bool SameDetailCode(string? a, string? b)
        {
            var aa = NormDetailCode(a);
            var bb = NormDetailCode(b);

            if (string.IsNullOrWhiteSpace(aa) || string.IsNullOrWhiteSpace(bb))
                return false;

            return aa == bb;
        }

        private static bool IsPrevBtpInputCode(string? code)
        {
            var c = NormDetailCode(code);

            return c is "PREV" or "INPUT" or "BTP" or "REFERENCE";
        }

        private static bool IsLaminationCode(string? code)
        {
            var c = NormDetailCode(code);

            return c.Contains("LAMINATION")
                || c.Contains("MANG")
                || c.Contains("CAN");
        }

        private static bool IsCoatingCode(string? code)
        {
            var c = NormDetailCode(code);

            return c.Contains("COATING")
                || c.Contains("KEO_PHU")
                || c.Contains("PHU");
        }

        private static bool IsMountingGlueCode(string? code)
        {
            var c = NormDetailCode(code);

            return c.Contains("KEO_BOI")
                || c.Contains("MOUNTING_GLUE");
        }

        private static bool IsInkCode(string? code)
        {
            var c = NormDetailCode(code);

            return c.Contains("INK")
                || c.Contains("MUC");
        }

        private static bool IsPlateCode(string? code)
        {
            var c = NormDetailCode(code);

            return c.Contains("PLATE")
                || c.Contains("BAN_KEM");
        }

        private static List<TaskMaterialUsageLogItemDto> ResolveMaterialUsagesFromLogs(
            List<TaskLogDto> logs)
        {
            return logs
                .SelectMany(log =>
                    log.material_usages != null && log.material_usages.Count > 0
                        ? log.material_usages
                        : ParseJsonArraySafe<TaskMaterialUsageLogItemDto>(log.material_usage_json))
                .ToList();
        }

        private static List<TaskReferenceUsageInputDto> ResolveReferenceInputsFromLogs(
            List<TaskLogDto> logs)
        {
            return logs
                .SelectMany(log =>
                    log.reference_inputs != null && log.reference_inputs.Count > 0
                        ? log.reference_inputs
                        : ParseJsonArraySafe<TaskReferenceUsageInputDto>(log.reference_input_json))
                .ToList();
        }

        private static List<TaskOutputReportDto> ResolveOutputsFromLogs(
            List<TaskLogDto> logs)
        {
            return logs
                .SelectMany(log =>
                    log.outputs != null && log.outputs.Count > 0
                        ? log.outputs
                        : ParseJsonArraySafe<TaskOutputReportDto>(log.output_json))
                .ToList();
        }

        private static int ResolveActualOutputQtyFromTaskLogs(List<TaskLogDto> logs)
        {
            var fromOutputJson = ResolveOutputsFromLogs(logs)
                .Sum(x => x.quantity_good);

            if (fromOutputJson > 0)
                return (int)Math.Ceiling(fromOutputJson);

            return logs.Sum(x => x.qty_good);
        }

        private static int ResolveActualBadQtyFromOutputJson(List<TaskLogDto> logs)
        {
            var bad = ResolveOutputsFromLogs(logs)
                .Sum(x => x.quantity_bad);

            return bad <= 0 ? 0 : (int)Math.Ceiling(bad);
        }

        private static void ApplyTaskLogJsonToProductionStage(
            ProductionStageDto stage,
            List<TaskLogDto> logs)
        {
            if (stage == null || logs == null || logs.Count == 0)
                return;

            var materialUsages = ResolveMaterialUsagesFromLogs(logs);
            var referenceInputs = ResolveReferenceInputsFromLogs(logs);
            var outputs = ResolveOutputsFromLogs(logs);

            /*
             * 1. Map input_materials từ material_usage_json / reference_input_json.
             */
            if (stage.input_materials != null && stage.input_materials.Count > 0)
            {
                foreach (var input in stage.input_materials)
                {
                    var inputCode = NormDetailCode(input.code);

                    /*
                     * BTP đầu vào từ công đoạn trước:
                     * ưu tiên reference_input_json.quantity_used.
                     */
                    var refMatches = referenceInputs
                        .Where(x =>
                            SameDetailCode(x.input_code, input.code)
                            || IsPrevBtpInputCode(input.code))
                        .ToList();

                    if (refMatches.Count > 0)
                    {
                        var used = refMatches.Sum(x => x.quantity_used);
                        var totalEstimate = refMatches.Sum(x => x.quantity_used + x.quantity_left);

                        if (used > 0)
                        {
                            input.actual_quantity = Math.Round(used, 4);
                            input.quantity_source = "Actual";
                        }

                        if ((input.estimated_quantity <= 0 || input.estimated_quantity == null) &&
                            totalEstimate > 0)
                        {
                            input.estimated_quantity = Math.Round(totalEstimate, 4);
                        }

                        var firstRef = refMatches.FirstOrDefault();
                        if (firstRef != null)
                        {
                            if (string.IsNullOrWhiteSpace(input.name))
                                input.name = firstRef.input_name;

                            if (string.IsNullOrWhiteSpace(input.unit))
                                input.unit = firstRef.unit;
                        }

                        continue;
                    }

                    /*
                     * NVL kho: map từ material_usage_json.
                     */
                    var materialMatches = materialUsages
                        .Where(x =>
                            SameDetailCode(x.material_code, input.code)
                            || SameDetailCode(x.material_name, input.name)
                            || (IsLaminationCode(inputCode) && IsLaminationCode(x.material_code))
                            || (IsCoatingCode(inputCode) && IsCoatingCode(x.material_code))
                            || (IsMountingGlueCode(inputCode) && IsMountingGlueCode(x.material_code))
                            || (IsInkCode(inputCode) && IsInkCode(x.material_code))
                            || (IsPlateCode(inputCode) && IsPlateCode(x.material_code)))
                        .ToList();

                    if (materialMatches.Count == 0)
                        continue;

                    var materialUsed = materialMatches.Sum(x => x.quantity_used);
                    var materialEstimated = materialMatches.Sum(x =>
                        x.estimated_input_qty > 0
                            ? x.estimated_input_qty
                            : x.quantity_used + x.quantity_left + x.quantity_waste);

                    if (materialUsed > 0)
                    {
                        input.actual_quantity = Math.Round(materialUsed, 4);
                        input.quantity_source = "Actual";
                    }

                    if ((input.estimated_quantity <= 0 || input.estimated_quantity == null) &&
                        materialEstimated > 0)
                    {
                        input.estimated_quantity = Math.Round(materialEstimated, 4);
                    }

                    var firstMaterial = materialMatches.FirstOrDefault();
                    if (firstMaterial != null)
                    {
                        if (string.IsNullOrWhiteSpace(input.code))
                            input.code = firstMaterial.material_code;

                        if (string.IsNullOrWhiteSpace(input.name))
                            input.name = firstMaterial.material_name;

                        if (string.IsNullOrWhiteSpace(input.unit))
                            input.unit = firstMaterial.unit;
                    }
                }
            }

            /*
             * 2. Map output_product từ output_json.
             */
            if (stage.output_product != null)
            {
                var outputMatches = outputs
                    .Where(x =>
                        SameDetailCode(x.output_code, stage.output_product.code)
                        || SameDetailCode(x.output_name, stage.output_product.name))
                    .ToList();

                if (outputMatches.Count == 0 && outputs.Count > 0)
                    outputMatches = outputs;

                if (outputMatches.Count > 0)
                {
                    var good = outputMatches.Sum(x => x.quantity_good);
                    var bad = outputMatches.Sum(x => x.quantity_bad);

                    if (good > 0)
                    {
                        stage.output_product.actual_quantity = Math.Round(good, 4);
                        stage.output_product.quantity_source = "Actual";

                        stage.actual_output_quantity = Math.Round(good, 4);
                        stage.qty_good = (int)Math.Ceiling(good);
                    }

                    if (bad > 0)
                    {
                        var denom = good + bad;
                        stage.waste_percent = denom <= 0
                            ? 0m
                            : Math.Round((bad * 100m) / denom, 2);
                    }

                    var firstOutput = outputMatches.FirstOrDefault();
                    if (firstOutput != null)
                    {
                        if (string.IsNullOrWhiteSpace(stage.output_product.code))
                            stage.output_product.code = firstOutput.output_code;

                        if (string.IsNullOrWhiteSpace(stage.output_product.name))
                            stage.output_product.name = firstOutput.output_name;

                        if (string.IsNullOrWhiteSpace(stage.output_product.unit))
                            stage.output_product.unit = firstOutput.unit;
                    }
                }
                else if (stage.qty_good > 0)
                {
                    stage.output_product.actual_quantity = stage.qty_good;
                    stage.actual_output_quantity = stage.qty_good;
                }

                if (stage.estimated_output_quantity <= 0 &&
                    stage.output_product.estimated_quantity > 0)
                {
                    stage.estimated_output_quantity = stage.output_product.estimated_quantity;
                }
            }
        }

        public async Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var header = await (
                from pr in _db.productions.AsNoTracking()
                join o in _db.orders.AsNoTracking() on pr.order_id equals o.order_id into oj
                from o in oj.DefaultIfEmpty()

                join r in _db.order_requests.AsNoTracking() on o.order_id equals r.order_id into rj
                from r in rj.DefaultIfEmpty()

                join q in _db.quotes.AsNoTracking() on o.quote_id equals q.quote_id into qj
                from q in qj.DefaultIfEmpty()

                join pt in _db.product_types.AsNoTracking() on pr.product_type_id equals pt.product_type_id into ptj
                from pt in ptj.DefaultIfEmpty()

                join sp in _db.sub_products.AsNoTracking() on pr.sub_product_id equals sp.id into spj
                from sp in spj.DefaultIfEmpty()

                where pr.order_id == orderId
                orderby (pr.planned_start_date ?? pr.created_at ?? pr.end_date)
                select new
                {
                    pr,
                    sp,
                    o,
                    product_type_name = pt != null ? pt.name : null,
                    packaging_standard = pt != null ? pt.packaging_standard : null,
                    customer_name = !string.IsNullOrWhiteSpace(r.customer_name) ? r.customer_name : "Khách hàng",
                    first_item = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new
                        {
                            i.item_id,
                            i.product_name,
                            i.quantity,
                            i.production_process,
                            i_length = (int?)EF.Property<int?>(i, "length_mm"),
                            i_width = (int?)EF.Property<int?>(i, "width_mm"),
                            i_height = (int?)EF.Property<int?>(i, "height_mm"),
                            i_ink_weight_kg = (decimal?)i.est_ink_weight_kg
                        })
                        .FirstOrDefault()
                }).FirstOrDefaultAsync(ct);

            if (header == null)
                return null;

            var dto = new ProductionDetailDto
            {
                prod_id = header.pr.prod_id,
                import_recieve_path = header.pr.import_recieve_path,
                production_code = header.pr.code,
                production_status = header.pr.status,
                created_at = header.pr.created_at,
                planned_start_date = header.pr.planned_start_date,
                actual_start_date = header.pr.actual_start_date,
                end_date = header.pr.end_date,
                order_id = header.o?.order_id,
                order_code = header.o?.code,
                delivery_date = header.o?.delivery_date,
                customer_name = header.customer_name ?? "Khách ẩn tên",
                product_name = header.first_item?.product_name,
                quantity = header.first_item?.quantity ?? 0,
                product_type_id = header.pr.product_type_id,
                packaging_standard = header.packaging_standard,
                length_mm = header.first_item?.i_length,
                width_mm = header.first_item?.i_width,
                height_mm = header.first_item?.i_height,

                production_method = header.pr.prod_method,
                sub_product_id = header.pr.sub_product_id,
                sub_product_used_qty = header.pr.sub_product_used_qty,
                nvl_qty = header.pr.nvl_qty,
                sub_product_process = header.sp != null ? header.sp.product_process : null,
                is_full_process = header.pr.is_full_process,
                planned_end_date = header.pr.planned_end_date,
                production_approval_flow = header.pr.production_approval_flow,
                is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(header.pr.production_approval_flow),
                production_approval_label = ProductionApprovalFlowHelper.Label(header.pr.production_approval_flow)
            };

            order_request? orderReq = null;
            cost_estimate? estimate = null;

            int sheetsRequired = 0;
            int sheetsWaste = 0;
            int sheetsTotal = 0;
            int nUp = 1;

            if (dto.order_id.HasValue)
            {
                orderReq = await _db.order_requests
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == dto.order_id.Value, ct);

                if (orderReq != null)
                {
                    if (orderReq.accepted_estimate_id.HasValue && orderReq.accepted_estimate_id.Value > 0)
                    {
                        estimate = await _db.cost_estimates
                            .AsNoTracking()
                            .FirstOrDefaultAsync(
                                x => x.estimate_id == orderReq.accepted_estimate_id.Value
                                  && x.order_request_id == orderReq.order_request_id,
                                ct);
                    }

                    estimate ??= await _db.cost_estimates
                        .AsNoTracking()
                        .Where(x => x.order_request_id == orderReq.order_request_id)
                        .OrderByDescending(x => x.is_active)
                        .ThenByDescending(x => x.estimate_id)
                        .FirstOrDefaultAsync(ct);

                    if (estimate != null)
                    {
                        sheetsRequired = estimate.sheets_required;
                        sheetsWaste = estimate.sheets_waste;
                        sheetsTotal = estimate.sheets_total;
                        nUp = estimate.n_up > 0 ? estimate.n_up : 1;
                    }
                    dto.n_up = nUp;
                }
            }

            dto.ready_print_file = orderReq?.print_ready_file;
            dto.ink_type_names = estimate?.ink_type_names;
            dto.wave_type = estimate?.wave_type;
            dto.paper_name = estimate?.paper_name;
            dto.coating_type = estimate?.coating_type;
            dto.paper_alternative = estimate?.paper_alternative;
            dto.wave_alternative = estimate?.wave_alternative;
            dto.lamination_material_id = estimate?.lamination_material_id;
            dto.lamination_material_code = estimate?.lamination_material_code;
            dto.lamination_material_name = estimate?.lamination_material_name;
            decimal estInkWeightKg = 0m;
            if (header.first_item?.i_ink_weight_kg.HasValue == true)
                estInkWeightKg = header.first_item.i_ink_weight_kg.Value;

            int? numberOfPlates = orderReq?.number_of_plates;
            decimal estCoatingGlueWeightKg = estimate?.coating_glue_weight_kg ?? 0m;

            material? coatingMaterial = null;

            if (estimate != null &&
                estCoatingGlueWeightKg > 0m &&
                !IsNoCoatingType(estimate.coating_type))
            {
                coatingMaterial = await ResolveCoatingMaterialForDetailAsync(estimate, ct);
            }

            var coatingMaterialCode = coatingMaterial?.code
                ?? ResolveCoatingMaterialCodeForDetail(estimate?.coating_type);

            var coatingMaterialName = coatingMaterial?.name
                ?? (!IsNoCoatingType(estimate?.coating_type)
                    ? ProductionFlowHelper.ResolveCoatingDisplayName(estimate?.coating_type)
                    : null);

            var coatingMaterialUnit = coatingMaterial?.unit ?? "kg";
            var prodId = header.pr.prod_id;

            var tasks = await _db.tasks.AsNoTracking()
    .Where(t => t.prod_id == prodId)
    .Select(t => new
    {
        t.task_id,
        t.prod_id,
        t.seq_num,
        t.name,
        t.status,
        t.machine,
        t.start_time,
        t.end_time,
        t.planned_start_time,
        t.planned_end_time,
        t.process_id,
        t.is_taken_sub_product
    })
    .ToListAsync(ct);

            var lastTask = tasks
                .OrderByDescending(t => t.seq_num ?? 0)
                .ThenByDescending(t => t.task_id)
                .FirstOrDefault();

            if (lastTask != null
    && string.Equals(lastTask.status, "Finished", StringComparison.OrdinalIgnoreCase)
    && lastTask.end_time != null
    && !string.Equals(header.pr.status, "Importing", StringComparison.OrdinalIgnoreCase))
            {
                var prodToUpdate = new production { prod_id = prodId };
                _db.productions.Attach(prodToUpdate);

                prodToUpdate.status = "Importing";
                prodToUpdate.end_date = lastTask.end_time;

                if (header.pr.actual_start_date == null)
                    prodToUpdate.actual_start_date = lastTask.end_time;

                await _db.SaveChangesAsync(ct);

                dto.production_status = "Importing";
                dto.end_date = lastTask.end_time;
                dto.actual_start_date ??= lastTask.end_time;
            }

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs.AsNoTracking()
                  .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                  .OrderBy(l => l.log_time)
                  .Select(l => new TaskLogDto
                  {
                      log_id = l.log_id,
                      task_id = l.task_id!.Value,
                      action_type = l.action_type,
                      qty_good = l.qty_good ?? 0,
                      log_time = l.log_time,
                      scanned_code = l.scanned_code,
                      scanned_by_user_id = l.scanned_by_user_id,

                      reason = l.reason,
                      comment = l.reason,

                      report_image_url = l.report_image_url,

                      material_usage_json = l.material_usage_json,
                      reference_input_json = l.reference_input_json,
                      output_json = l.output_json
                  })
                  .ToListAsync(ct);

            foreach (var log in logs)
            {
                log.report_image_urls = SplitImageUrls(log.report_image_url);

                if (!string.IsNullOrWhiteSpace(log.material_usage_json))
                {
                    try
                    {
                        log.material_usages = JsonSerializer.Deserialize<List<TaskMaterialUsageLogItemDto>>(
                            log.material_usage_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            }) ?? new List<TaskMaterialUsageLogItemDto>();
                    }
                    catch
                    {
                        log.material_usages = new List<TaskMaterialUsageLogItemDto>();
                    }
                }
                else
                {
                    log.material_usages = new List<TaskMaterialUsageLogItemDto>();
                }

                if (!string.IsNullOrWhiteSpace(log.reference_input_json))
                {
                    try
                    {
                        log.reference_inputs = JsonSerializer.Deserialize<List<TaskReferenceUsageInputDto>>(
                            log.reference_input_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            }) ?? new List<TaskReferenceUsageInputDto>();
                    }
                    catch
                    {
                        log.reference_inputs = new List<TaskReferenceUsageInputDto>();
                    }
                }
                else
                {
                    log.reference_inputs = new List<TaskReferenceUsageInputDto>();
                }

                if (!string.IsNullOrWhiteSpace(log.output_json))
                {
                    try
                    {
                        log.outputs = JsonSerializer.Deserialize<List<TaskOutputReportDto>>(
                            log.output_json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            }) ?? new List<TaskOutputReportDto>();
                    }
                    catch
                    {
                        log.outputs = new List<TaskOutputReportDto>();
                    }
                }
                else
                {
                    log.outputs = new List<TaskOutputReportDto>();
                }
            }

            var ptId = header.pr.product_type_id;
            List<ProductTypeProcessStepDto> steps = new();

            if (ptId.HasValue)
            {
                steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .OrderBy(p => p.seq_num)
                    .Select(p => new ProductTypeProcessStepDto
                    {
                        process_id = p.process_id,
                        seq_num = p.seq_num,
                        process_name = p.process_name,
                        process_code = EF.Property<string?>(p, "process_code"),
                        machine = p.machine
                    })
                    .ToListAsync(ct);
            }

            steps = ResolveFixedRoute(
                steps.OrderBy(x => x.seq_num).ToList(),
                x => x.process_code,
                header.first_item?.production_process
            );

            var stages = new List<ProductionStageDto>();
            StageOutputRef? prevOutput = null;
            var routeCodes = steps.Select(x => x.process_code).ToList();

            for (var stageIndex = 0; stageIndex < steps.Count; stageIndex++)
            {
                var s = steps[stageIndex];
                var pcode = (s.process_code ?? "").Trim().ToUpperInvariant();

                var task = tasks.FirstOrDefault(t => t.process_id == s.process_id)
                           ?? tasks.FirstOrDefault(t => (t.seq_num ?? -1) == s.seq_num);

                var stageLogs = task == null
                    ? new List<TaskLogDto>()
                    : LogsByTaskId(logs, task.task_id);

                var qtyGood = stageLogs.Sum(x => x.qty_good);
                var qtyBad = 0;
                var denom = qtyGood + qtyBad;
                var wastePct = denom <= 0 ? 0m : Math.Round((qtyBad * 100m) / denom, 2);

                var io = BuildStageIO(
    processCode: pcode,
    processName: s.process_name ?? "",
    detail: dto,
    prevOutput: prevOutput,
    sheetsRequired: sheetsRequired,
    sheetsWaste: sheetsWaste,
    sheetsTotal: sheetsTotal,
    nUp: nUp,
    qtyGood: qtyGood,
    numberOfPlates: numberOfPlates,
    estInkWeightKg: estInkWeightKg,
    currentStageIndex: stageIndex,
    routeProcessCodes: routeCodes,
    paperCode: estimate?.paper_code,
    paperName: estimate?.paper_name,
    waveType: estimate?.wave_type,
    coatingType: estimate?.coating_type,
    coatingMaterialCode: coatingMaterialCode,
    coatingMaterialName: coatingMaterialName,
    coatingMaterialUnit: coatingMaterialUnit,
    estCoatingGlueWeightKg: estCoatingGlueWeightKg,
    inkTypeNames: estimate?.ink_type_names,
    laminationMaterialCode: estimate?.lamination_material_code,
    laminationMaterialName: estimate?.lamination_material_name,
    estLaminationWeightKg: estimate?.lamination_weight_kg ?? 0m
);

                var stage = new ProductionStageDto
                {
                    process_id = s.process_id,
                    seq_num = s.seq_num,
                    process_name = s.process_name ?? "",
                    process_code = s.process_code,
                    machine = task?.machine ?? s.machine,
                    task_id = task?.task_id,
                    task_name = task?.name,
                    status = task?.status,
                    start_time = task?.start_time,
                    end_time = task?.end_time,
                    qty_good = qtyGood,
                    waste_percent = wastePct,
                    last_scan_time = stageLogs.Count == 0 ? null : stageLogs.Max(x => x.log_time),
                    logs = stageLogs,
                    input_materials = io.inputs,
                    output_product = io.output,
                    planned_start_time = task?.planned_start_time,
                    planned_end_time = task?.planned_end_time,
                    estimated_output_quantity = io.output.estimated_quantity,
                    actual_output_quantity = io.output.actual_quantity,
                    n_up = nUp,
                    is_taken_sub_product = task?.is_taken_sub_product ?? false
                };

                ApplyTaskLogJsonToProductionStage(stage, stageLogs);
                ApplyOutputActualFallbackFromQtyGood(stage);

                await ApplySubFirstDownstreamActualEstimateAsync(
                    header.pr,
                    stage,
                    steps,
                    ct);

                NormalizeNullActualInputMaterials(stage);

                stages.Add(stage);

                prevOutput = new StageOutputRef
                {
                    Name = stage.output_product?.name ?? io.nextOutput.Name,
                    Code = stage.output_product?.code ?? io.nextOutput.Code,
                    Unit = stage.output_product?.unit ?? io.nextOutput.Unit,

                    EstimatedQuantity =
                        stage.output_product?.estimated_quantity > 0
                            ? stage.output_product.estimated_quantity
                            : io.nextOutput.EstimatedQuantity,

                    ActualQuantity =
        stage.output_product != null && stage.output_product.actual_quantity > 0
            ? stage.output_product.actual_quantity
            : stage.qty_good > 0
                ? stage.qty_good
                : io.nextOutput.ActualQuantity
                };
            }

            dto.stages = stages;
            return dto;
        }

        private static List<string> SplitImageUrls(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default)
        {
            var prod = await _db.productions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null) return null;

            var tasks = await _db.tasks.AsNoTracking()
                .Where(t => t.prod_id == prodId)
                .Select(t => new { t.task_id, t.seq_num, t.process_id, t.name })
                .ToListAsync(ct);

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs.AsNoTracking()
                .Where(l => l.task_id != null && taskIds.Contains(l.task_id.Value))
                .Select(l => new
                {
                    task_id = l.task_id!.Value,
                    qty_good = l.qty_good ?? 0,
                    log_time = l.log_time
                })
                .ToListAsync(ct);

            var ptId = prod.product_type_id;
            var stepMeta = new Dictionary<int, (string? code, string name, int seq)>();

            if (ptId.HasValue)
            {
                var steps = await _db.product_type_processes.AsNoTracking()
                    .Where(p => p.product_type_id == ptId.Value && (p.is_active ?? true))
                    .Select(p => new
                    {
                        p.process_id,
                        p.process_name,
                        p.seq_num,
                        process_code = (string?)EF.Property<string?>(p, "process_code")
                    })
                    .ToListAsync(ct);

                stepMeta = steps.ToDictionary(
                    x => x.process_id,
                    x => (x.process_code, x.process_name ?? "", x.seq_num)
                );
            }

            var stageRows = new List<StageWasteDto>();

            foreach (var t in tasks.OrderBy(x => x.seq_num ?? int.MaxValue))
            {
                var tlogs = logs.Where(x => x.task_id == t.task_id).ToList();
                var good = tlogs.Sum(x => x.qty_good);

                string pname = t.name;
                string? pcode = null;
                var seq = t.seq_num ?? 0;

                if (t.process_id.HasValue && stepMeta.TryGetValue(t.process_id.Value, out var meta))
                {
                    pname = meta.name;
                    pcode = meta.code;
                    seq = meta.seq;
                }

                stageRows.Add(new StageWasteDto
                {
                    task_id = t.task_id,
                    seq_num = seq,
                    process_name = pname,
                    process_code = pcode,
                    qty_good = good,
                    first_scan = tlogs.Count == 0 ? null : tlogs.Min(x => x.log_time),
                    last_scan = tlogs.Count == 0 ? null : tlogs.Max(x => x.log_time),
                });
            }

            var totalGood = stageRows.Sum(x => (decimal)x.qty_good);
            var totalDenom = totalGood;

            return new ProductionWasteReportDto
            {
                prod_id = prodId,
                total_good = totalGood,
                stages = stageRows.OrderBy(x => x.seq_num).ToList()
            };
        }

        public async Task<int?> StartProductionByProdIdOnlyAsync(
    int prodId,
    DateTime now,
    CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var prod = await _db.productions
                    .FirstOrDefaultAsync(p => p.prod_id == prodId, ct);

                if (prod == null)
                    return (int?)null;

                if (string.Equals(prod.status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prod.status, "Delivery", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prod.status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Không thể bắt đầu production vì production đang ở trạng thái {prod.status}.");
                }

                if (string.Equals(prod.status, "InProcessing", StringComparison.OrdinalIgnoreCase))
                {
                    await tx.CommitAsync(ct);
                    return prod.prod_id;
                }

                prod.status = "InProcessing";
                prod.actual_start_date ??= now;

                if (prod.created_at == null)
                    prod.created_at = now;

                if (prod.planned_start_date == null)
                    prod.planned_start_date = now;

                if (IsGroupProductionKind(prod.prod_kind))
                {
                    await SyncGroupMemberOrdersToInProcessingAsync(
                        prod.prod_id,
                        ct);
                }
                else
                {
                    await SyncDirectProductionOrderToInProcessingAsync(
                        prod,
                        ct);
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return prod.prod_id;
            });
        }

        public async Task<bool> StartProductionByProdIdAsync(
            int prodId,
            DateTime now,
            CancellationToken ct = default)
        {
            var startedProdId = await StartProductionByProdIdOnlyAsync(
                prodId,
                now,
                ct);

            return startedProdId.HasValue;
        }

        private async Task<bool> HasUnfinishedGroupLinksForSingleProductionAsync(
    int prodId,
    CancellationToken ct)
        {
            var activeLinks = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.single_prod_id == prodId &&
                    (
                        x.status == null ||
                        (
                            x.status.Trim().ToUpper() != "CANCELLED" &&
                            x.status.Trim().ToUpper() != "DONE"
                        )
                    ))
                .Select(x => new
                {
                    x.group_task_id,
                    x.process_code,
                    x.order_id
                })
                .ToListAsync(ct);

            if (activeLinks.Count == 0)
                return false;

            var groupTaskIds = activeLinks
                .Select(x => x.group_task_id)
                .Distinct()
                .ToList();

            var finishedGroupTaskIds = await _db.tasks
                .AsNoTracking()
                .Where(x =>
                    groupTaskIds.Contains(x.task_id) &&
                    x.status != null &&
                    x.status.Trim().ToUpper() == "FINISHED")
                .Select(x => x.task_id)
                .ToListAsync(ct);

            var finishedSet = finishedGroupTaskIds.ToHashSet();

            return activeLinks.Any(x => !finishedSet.Contains(x.group_task_id));
        }

        public async Task<bool> TryCloseProductionIfCompletedAsync(
    int prodId,
    DateTime now,
    CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(p => p.prod_id == prodId, ct);

            if (prod == null)
                return false;

            var tasks = await _db.tasks
                .Where(t => t.prod_id == prodId)
                .Select(t => new
                {
                    t.task_id,
                    t.status,
                    t.end_time,
                    t.seq_num
                })
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return false;

            var allOwnTasksFinished = tasks.All(t =>
                string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                t.end_time != null);

            if (!allOwnTasksFinished)
                return false;

            var finishedAt = tasks
                .Where(t => t.end_time != null)
                .Select(t => t.end_time!.Value)
                .DefaultIfEmpty(now)
                .Max();

            /*
             * FIX CHÍNH:
             * Không chặn production SINGLE/SPLIT chuyển Importing vì còn group link chưa xong.
             *
             * Production là 1 batch sản xuất.
             * Nếu batch đó đã xong hết task của chính nó thì batch đó phải Importing.
             *
             * Order/request chỉ được Importing khi full path của order xong,
             * phần đó được kiểm soát bằng CanMoveOrderToImportingByFullPathAsync().
             */
            var changed = false;

            if (!string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prod.status, "Finished", StringComparison.OrdinalIgnoreCase))
            {
                prod.status = "Importing";
                changed = true;
            }

            if (prod.end_date == null)
            {
                prod.end_date = finishedAt;
                changed = true;
            }

            if (prod.actual_start_date == null)
            {
                prod.actual_start_date = finishedAt;
                changed = true;
            }

            var isGroupProduction = string.Equals(
                prod.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

            if (isGroupProduction)
            {
                /*
                 * GROUP xong chỉ đóng production GROUP.
                 * Order/request chỉ lên Importing nếu full path của từng order đã xong.
                 */
                await SyncGroupMemberOrdersToImportingAsync(
                    prod,
                    finishedAt,
                    ct);
            }
            else
            {
                /*
                 * SINGLE/SPLIT xong thì đóng chính production đó.
                 * Order/request chỉ lên Importing nếu full path của order đã xong.
                 */
                await SyncSingleProductionOrderToImportingAsync(
                    prod,
                    finishedAt,
                    ct);
            }

            await _db.SaveChangesAsync(ct);

            return changed;
        }

        private async Task SyncSingleProductionOrderToImportingAsync(
    production prod,
    DateTime finishedAt,
    CancellationToken ct)
        {
            if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                return;

            var canMoveOrder = await CanMoveOrderToImportingByFullPathAsync(
                prod.order_id.Value,
                ct);

            if (!canMoveOrder)
                return;

            await SyncOrderAndRequestsToImportingAsync(
                prod.order_id.Value,
                finishedAt,
                ct);
        }

        private async Task SyncGroupMemberOrdersToImportingAsync(
    production groupProd,
    DateTime finishedAt,
    CancellationToken ct)
        {
            var members = await _db.prod_orders
                .Where(x =>
                    x.prod_id == groupProd.prod_id &&
                    x.status == "Active")
                .ToListAsync(ct);

            foreach (var member in members)
            {
                /*
                 * Nếu single production gốc của order cũng đã xong hết task riêng
                 * thì cho single production đó Importing.
                 *
                 * Nhưng KHÔNG được kéo order lên Importing tại đây nếu full path chưa xong.
                 */
                if (member.single_prod_id.HasValue && member.single_prod_id.Value > 0)
                {
                    var singleProd = await _db.productions
                        .FirstOrDefaultAsync(x => x.prod_id == member.single_prod_id.Value, ct);

                    if (singleProd != null)
                    {
                        var singleAllFinished = await AreAllProductionTasksFinishedAsync(
                            singleProd.prod_id,
                            ct);

                        if (singleAllFinished)
                        {
                            await MarkProductionRowImportingAsync(
                                singleProd,
                                finishedAt,
                                ct);
                        }
                    }
                }

                /*
                 * Order/request chỉ Importing khi full path của order đã xong.
                 */
                var canMoveOrder = await CanMoveOrderToImportingByFullPathAsync(
                    member.order_id,
                    ct);

                if (!canMoveOrder)
                    continue;

                await SyncOrderAndRequestsToImportingAsync(
                    member.order_id,
                    finishedAt,
                    ct);
            }
        }

        private Task MarkProductionRowImportingAsync(
    production prod,
    DateTime finishedAt,
    CancellationToken ct)
        {
            if (prod == null)
                return Task.CompletedTask;

            if (!string.Equals(prod.status, "Finished", StringComparison.OrdinalIgnoreCase))
            {
                prod.status = "Importing";
            }

            prod.end_date ??= finishedAt;
            prod.actual_start_date ??= finishedAt;

            return Task.CompletedTask;
        }

        private async Task<bool> CanMoveOrderToImportingByFullPathAsync(
    int orderId,
    CancellationToken ct)
        {
            if (orderId <= 0)
                return false;

            var routeCodes = await GetOrderFullProductionPathAsync(
                orderId,
                ct);

            if (routeCodes.Count == 0)
                return false;

            routeCodes = routeCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (routeCodes.Count == 0)
                return false;

            /*
             * Chỉ khi công đoạn cuối của full path xong mới xét đưa order/request Importing.
             * Ví dụ full path cuối là DAN thì DAN phải Finished.
             */
            var finalProcessCode = routeCodes.Last();

            var finalFinished = await IsOrderProcessFinishedAsync(
                orderId,
                finalProcessCode,
                ct);

            if (!finalFinished)
                return false;

            /*
             * Check lại toàn bộ công đoạn trong full path.
             * Bao gồm:
             * - task trực tiếp trong SINGLE/SPLIT
             * - group task thông qua task_links/task_qtys
             */
            foreach (var code in routeCodes)
            {
                var finished = await IsOrderProcessFinishedAsync(
                    orderId,
                    code,
                    ct);

                if (!finished)
                    return false;
            }

            return true;
        }

        private async Task<List<string>> GetOrderFullProductionPathAsync(
    int orderId,
    CancellationToken ct)
        {
            if (orderId <= 0)
                return new List<string>();

            var item = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.product_type_id,
                    x.production_process
                })
                .FirstOrDefaultAsync(ct);

            var fromOrderItem = ParseProcessCodes(item?.production_process);

            if (fromOrderItem.Count > 0)
                return fromOrderItem;

            if (item?.product_type_id == null || item.product_type_id.Value <= 0)
                return new List<string>();

            var fromMaster = await _db.product_type_processes
                .AsNoTracking()
                .Where(x =>
                    x.product_type_id == item.product_type_id.Value &&
                    (x.is_active ?? true))
                .OrderBy(x => x.seq_num)
                .Select(x => x.process_code)
                .ToListAsync(ct);

            return fromMaster
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private static readonly string[] FullRouteOrder =
{
    "RALO", "CAT", "IN", "PHU", "CAN", "BOI", "BE", "DUT", "DAN"
};

        private static string NormProcessCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static int FullRouteIndex(string? value)
        {
            var code = NormProcessCode(value);

            var idx = Array.FindIndex(
                FullRouteOrder,
                x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

            return idx < 0 ? 999 : idx;
        }

        private static List<string> ParseProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private async Task<bool> IsOrderProcessFinishedAsync(
    int orderId,
    string? processCode,
    CancellationToken ct)
        {
            var code = NormProcessCode(processCode);

            if (orderId <= 0 || string.IsNullOrWhiteSpace(code))
                return false;

            /*
             * CASE 1:
             * Công đoạn nằm trong production riêng SINGLE/SPLIT.
             */
            var directFinished = await (
                from t in _db.tasks.AsNoTracking()

                join p in _db.productions.AsNoTracking()
                    on t.prod_id equals p.prod_id

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where p.order_id == orderId
                      && p.status != "Cancelled"
                      && pp != null
                      && pp.process_code != null
                      && pp.process_code.Trim().ToUpper() == code
                      &&
                      (
                          t.status != null &&
                          t.status.Trim().ToUpper() == "FINISHED"
                          ||
                          t.end_time != null
                      )

                select t.task_id
            ).AnyAsync(ct);

            if (directFinished)
                return true;

            /*
             * CASE 2:
             * Công đoạn nằm trong GROUP production.
             * Với group task, sản lượng từng order được mirror vào task_qtys.
             */
            var groupQtyFinished = await _db.task_qtys
                .AsNoTracking()
                .AnyAsync(x =>
                    x.order_id == orderId &&
                    x.process_code != null &&
                    x.process_code.Trim().ToUpper() == code &&
                    x.qty_good > 0,
                    ct);

            if (groupQtyFinished)
                return true;

            /*
             * CASE 3:
             * Fallback theo task_links + group task Finished.
             * Dùng để bao phủ trường hợp task_qtys chưa có nhưng link đã Done.
             */
            var groupTaskFinished = await (
                from link in _db.task_links.AsNoTracking()

                join gt in _db.tasks.AsNoTracking()
                    on link.group_task_id equals gt.task_id

                join gp in _db.productions.AsNoTracking()
                    on gt.prod_id equals gp.prod_id

                where link.order_id == orderId
                      && link.process_code != null
                      && link.process_code.Trim().ToUpper() == code
                      && gp.prod_kind == "GROUP"
                      && gp.status != "Cancelled"
                      &&
                      (
                          link.status != null &&
                          link.status.Trim().ToUpper() == "DONE"
                          ||
                          gt.status != null &&
                          gt.status.Trim().ToUpper() == "FINISHED"
                          ||
                          gt.end_time != null
                      )

                select link.id
            ).AnyAsync(ct);

            return groupTaskFinished;
        }

        private static List<string> ParseProcessCodesForImportingCheck(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeProcessCodeForImportingCheck)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeProcessCodeForImportingCheck(string? raw)
        {
            return (raw ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static bool IsFinishedForImportingCheck(
            string? status,
            DateTime? endTime)
        {
            return string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase)
                   || endTime != null;
        }

        private async Task<bool> AreAllProductionTasksFinishedAsync(
    int prodId,
    CancellationToken ct)
        {
            if (prodId <= 0)
                return false;

            var tasks = await _db.tasks
                .AsNoTracking()
                .Where(x => x.prod_id == prodId)
                .Select(x => new
                {
                    x.task_id,
                    x.status,
                    x.end_time
                })
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return false;

            return tasks.All(x =>
                string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                x.end_time != null);
        }

        private async Task SyncOrderAndRequestsToImportingAsync(
    int orderId,
    DateTime finishedAt,
    CancellationToken ct)
        {
            if (orderId <= 0)
                return;

            /*
             * Chốt lại lần cuối để tránh order bị kéo lên Importing sớm.
             */
            var canMoveOrder = await CanMoveOrderToImportingByFullPathAsync(
                orderId,
                ct);

            if (!canMoveOrder)
                return;

            var ord = await _db.orders
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (ord == null)
                return;

            /*
             * Task finish chỉ đưa về Importing.
             * Không set Finished ở đây.
             * Finished chỉ do warehouse/import confirmation xử lý.
             */
            if (!string.Equals(ord.status, "Finished", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ord.status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                ord.status = "Importing";
            }

            var requests = await _db.order_requests
                .Where(x => x.order_id == orderId)
                .ToListAsync(ct);

            foreach (var req in requests)
            {
                if (!string.Equals(req.process_status, "Finished", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(req.process_status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    req.process_status = "Importing";
                }
            }

            /*
             * Khi full path của order đã xong, đồng bộ tất cả production liên quan đã hoàn thành
             * về Importing nếu có production nào còn InProcessing do data cũ.
             */
            await SyncAllCompletedProductionsOfOrderToImportingAsync(
                orderId,
                finishedAt,
                ct);
        }

        private async Task SyncAllCompletedProductionsOfOrderToImportingAsync(
    int orderId,
    DateTime finishedAt,
    CancellationToken ct)
        {
            if (orderId <= 0)
                return;

            /*
             * 1. SINGLE/SPLIT có order_id trực tiếp.
             */
            var directProds = await _db.productions
                .Where(x =>
                    x.order_id == orderId &&
                    x.status != "Cancelled")
                .ToListAsync(ct);

            foreach (var prod in directProds)
            {
                var allTasksFinished = await AreAllProductionTasksFinishedAsync(
                    prod.prod_id,
                    ct);

                if (!allTasksFinished)
                    continue;

                await MarkProductionRowImportingAsync(
                    prod,
                    finishedAt,
                    ct);
            }

            /*
             * 2. GROUP production liên quan qua prod_orders.
             */
            var groupProdIds = await _db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.order_id == orderId &&
                    x.status == "Active")
                .Select(x => x.prod_id)
                .Distinct()
                .ToListAsync(ct);

            if (groupProdIds.Count == 0)
                return;

            var groupProds = await _db.productions
                .Where(x =>
                    groupProdIds.Contains(x.prod_id) &&
                    x.prod_kind == "GROUP" &&
                    x.status != "Cancelled")
                .ToListAsync(ct);

            foreach (var prod in groupProds)
            {
                var allTasksFinished = await AreAllProductionTasksFinishedAsync(
                    prod.prod_id,
                    ct);

                if (!allTasksFinished)
                    continue;

                await MarkProductionRowImportingAsync(
                    prod,
                    finishedAt,
                    ct);
            }
        }

        public async Task<bool> SetProductionDeliveryByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(p => p.order_id == orderId, ct);

            if (prod == null)
                return false;

            var order = await _db.orders
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null)
                return false;

            var request = await _db.order_requests
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (request == null)
                return false;

            var now = AppTime.NowVnUnspecified();

            prod.status = "Delivery";
            order.status = "Delivery";
            order.confirmed_delivery_at = now;
            request.process_status = "Delivery";

            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<bool> SetCompletedByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(p => p.order_id == orderId, ct);

            if (prod == null)
                return false;

            var order = await _db.orders
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (order == null)
                return false;

            var request = await _db.order_requests
                .FirstOrDefaultAsync(o => o.order_id == orderId, ct);

            if (request == null)
                return false;

            prod.status = "Completed";
            order.status = "Completed";
            request.process_status = "Completed";

            await _db.SaveChangesAsync(ct);
            return true;
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);

        private static string NormalizeProcessCode(string? code)
            => (code ?? "").Trim().ToUpperInvariant();

        private static HashSet<string> ParseSelectedProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => NormalizeProcessCode(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<T> ResolveFixedRoute<T>(
    List<T> allSteps,
    Func<T, string?> processCodeSelector,
    string? selectedProcessesCsv)
        {
            if (allSteps == null || allSteps.Count == 0)
                return new List<T>();

            var selected = ParseSelectedProcessCodes(selectedProcessesCsv);
            if (selected.Count == 0)
                return allSteps;

            var filtered = allSteps
                .Where(x => selected.Contains(NormalizeProcessCode(processCodeSelector(x))))
                .ToList();

            return filtered.Count > 0 ? filtered : allSteps;
        }

        private static (List<StageMaterialDto> inputs, StageMaterialDto output, StageOutputRef nextOutput) BuildStageIO(
    string processCode,
    string processName,
    ProductionDetailDto detail,
    StageOutputRef? prevOutput,
    int sheetsRequired,
    int sheetsWaste,
    int sheetsTotal,
    int nUp,
    int qtyGood,
    int? numberOfPlates,
    decimal estInkWeightKg,
    int currentStageIndex,
    IReadOnlyList<string?> routeProcessCodes,
    string? paperCode,
    string? paperName,
    string? waveType,
    string? coatingType,
    string? coatingMaterialCode,
    string? coatingMaterialName,
    string? coatingMaterialUnit,
    decimal estCoatingGlueWeightKg,
    string? inkTypeNames,
    string? laminationMaterialCode,
    string? laminationMaterialName,
    decimal estLaminationWeightKg)
        {
            var inputs = new List<StageMaterialDto>();

            var code = (processCode ?? "").Trim().ToUpperInvariant();
            var productName = string.IsNullOrWhiteSpace(detail.product_name)
                ? "sản phẩm"
                : detail.product_name.Trim();

            sheetsRequired = Math.Max(0, sheetsRequired);
            sheetsWaste = Math.Max(0, sheetsWaste);
            sheetsTotal = Math.Max(sheetsTotal, Math.Max(sheetsRequired + sheetsWaste, 1));
            nUp = Math.Max(nUp, 1);

            var plateQty = Math.Max(1, numberOfPlates ?? 1);
            var sheetQty = Math.Max(1, sheetsTotal);
            var productionOutputQty = StageQuantityHelper.GetProductCap(sheetQty, nUp);

            var qtyContext = ResolveBothStageQuantityContext(
                detail,
                processCode,
                currentStageIndex,
                routeProcessCodes,
                sheetQty,
                productionOutputQty);

            sheetQty = qtyContext.stage_sheet_qty;
            productionOutputQty = qtyContext.stage_output_qty;

            var resolvedPaperCode = string.IsNullOrWhiteSpace(paperCode)
                ? "PAPER"
                : paperCode.Trim();

            var resolvedPaperName = string.IsNullOrWhiteSpace(paperName)
                ? "Giấy in"
                : paperName.Trim();

            static decimal? ActualFromQtyGood(int qtyGood, decimal cap)
            {
                if (qtyGood <= 0)
                    return null;

                return Math.Min(qtyGood, cap);
            }

            static decimal? ActualFromPrevious(StageOutputRef? prevOutput, decimal cap)
            {
                if (prevOutput?.ActualQuantity == null)
                    return null;

                return Math.Min(prevOutput.ActualQuantity.Value, cap);
            }

            void AddMainInputFromPrevious(
                string fallbackName,
                string fallbackCode,
                decimal estimatedQty,
                string unit)
            {
                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: !string.IsNullOrWhiteSpace(prevOutput?.Name)
                        ? prevOutput!.Name
                        : fallbackName,
                    code: !string.IsNullOrWhiteSpace(prevOutput?.Code)
                        ? prevOutput!.Code
                        : fallbackCode,
                    estimatedQty: estimatedQty,
                    actualQty: ActualFromPrevious(prevOutput, estimatedQty),
                    unit: unit));
            }

            // =========================
            // RALO
            // input = số bản kẽm
            // output = số bản kẽm
            // =========================
            if (code == "RALO" || code == "RA_LO")
            {
                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: "Bản kẽm cần ralo",
                    code: "PLATE",
                    estimatedQty: plateQty,
                    actualQty: null,
                    unit: "bản"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bản kẽm đã ralo",
                    code: "RALO",
                    estimatedQty: plateQty,
                    actualQty: ActualFromQtyGood(qtyGood, plateQty),
                    unit: "bản");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, plateQty, output.actual_quantity));
            }

            // =========================
            // CAT
            // input = sheets_total
            // output = sheets_total * n_up
            // unit = tờ
            // =========================
            if (code == "CAT" || code == "CUT")
            {
                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: resolvedPaperName,
                    code: resolvedPaperCode,
                    estimatedQty: sheetQty,
                    actualQty: null,
                    unit: "tờ"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Giấy đã cắt",
                    code: "CAT",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // IN
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "IN")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Giấy đã cắt",
                    fallbackCode: "CAT",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                if (!string.IsNullOrWhiteSpace(inkTypeNames))
                {
                    inputs.Add(ProductionSHelper.BuildStageMaterial(
                        name: $"Mực in ({inkTypeNames.Trim()})",
                        code: "INK_TYPES",
                        estimatedQty: estInkWeightKg > 0 ? estInkWeightKg : 0m,
                        actualQty: null,
                        unit: "kg"));
                }

                if ((numberOfPlates ?? 0) > 0)
                {
                    inputs.Add(ProductionSHelper.BuildStageMaterial(
                        name: "Bản kẽm in",
                        code: "PLATE",
                        estimatedQty: numberOfPlates.Value,
                        actualQty: null,
                        unit: "bản"));
                }

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm in",
                    code: "IN",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // PHU
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "PHU")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm in",
                    fallbackCode: "IN",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                /*
                 * Trước đây chỉ hiện NVL phủ khi estCoatingGlueWeightKg > 0.
                 * Với GROUP, ta cần hiện loại phủ/keo phủ lấy từ order đại diện,
                 * dù estimatedQty có thể để 0.
                 */
                var hasCoatingMaterial =
                    !IsNoCoatingType(coatingType) &&
                    (
                        !string.IsNullOrWhiteSpace(coatingType) ||
                        !string.IsNullOrWhiteSpace(coatingMaterialCode) ||
                        !string.IsNullOrWhiteSpace(coatingMaterialName)
                    );

                if (hasCoatingMaterial)
                {
                    var resolvedCode = !string.IsNullOrWhiteSpace(coatingMaterialCode)
                        ? coatingMaterialCode.Trim()
                        : ResolveCoatingMaterialCodeForDetail(coatingType) ?? "COATING";

                    var resolvedName = !string.IsNullOrWhiteSpace(coatingMaterialName)
                        ? coatingMaterialName.Trim()
                        : ProductionFlowHelper.ResolveCoatingDisplayName(coatingType);

                    var resolvedUnit = !string.IsNullOrWhiteSpace(coatingMaterialUnit)
                        ? coatingMaterialUnit.Trim()
                        : "kg";

                    inputs.Add(ProductionSHelper.BuildStageMaterial(
                        name: resolvedName,
                        code: resolvedCode,
                        estimatedQty: estCoatingGlueWeightKg > 0m ? estCoatingGlueWeightKg : 0m,
                        actualQty: null,
                        unit: resolvedUnit));
                }

                var output = ProductionSHelper.BuildStageMaterial(
                    name: hasCoatingMaterial
                        ? "Bán thành phẩm phủ"
                        : "Bán thành phẩm qua công đoạn phủ",
                    code: "PHU",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // CAN
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "CAN" || code == "CAN_MANG")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm phủ",
                    fallbackCode: "PHU",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                var resolvedLaminationCode = string.IsNullOrWhiteSpace(laminationMaterialCode)
                    ? "LAMINATION"
                    : laminationMaterialCode.Trim();

                var resolvedLaminationName = string.IsNullOrWhiteSpace(laminationMaterialName)
                    ? "Màng cán"
                    : laminationMaterialName.Trim();

                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: resolvedLaminationName,
                    code: resolvedLaminationCode,
                    estimatedQty: estLaminationWeightKg > 0 ? estLaminationWeightKg : 0m,
                    actualQty: null,
                    unit: "kg"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm đã cán",
                    code: "CAN",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // BOI
            // input/output chính = sheets_total * n_up, unit = tờ
            // =========================
            if (code == "BOI")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước bồi",
                    fallbackCode: "PREV",
                    estimatedQty: productionOutputQty,
                    unit: "tờ");

                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: string.IsNullOrWhiteSpace(waveType) ? "Sóng carton" : waveType.Trim(),
                    code: string.IsNullOrWhiteSpace(waveType) ? "WAVE" : waveType.Trim(),
                    estimatedQty: 0m,
                    actualQty: null,
                    unit: "tờ"));

                inputs.Add(ProductionSHelper.BuildStageMaterial(
                    name: "Keo bồi",
                    code: "KEO_BOI",
                    estimatedQty: 0m,
                    actualQty: null,
                    unit: "kg"));

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm đã bồi",
                    code: "BOI",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "tờ");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // BE
            // input/output = sheets_total * n_up, unit = sp
            // =========================
            if (code == "BE")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước bế",
                    fallbackCode: "PREV",
                    estimatedQty: productionOutputQty,
                    unit: "sp");

                var output = ProductionSHelper.BuildStageMaterial(
                    name: "Bán thành phẩm đã bế",
                    code: "BE",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "sp");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // DUT
            // input/output = sheets_total * n_up, unit = sp
            // =========================
            if (code == "DUT")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước dứt",
                    fallbackCode: "BE",
                    estimatedQty: productionOutputQty,
                    unit: "sp");

                var output = ProductionSHelper.BuildStageMaterial(
                    name: $"Bán thành phẩm đã dứt {productName}",
                    code: "DUT",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "sp");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // =========================
            // DAN
            // input/output = sheets_total * n_up, unit = sp
            // =========================
            if (code == "DAN")
            {
                AddMainInputFromPrevious(
                    fallbackName: "Bán thành phẩm trước dán",
                    fallbackCode: "DUT",
                    estimatedQty: productionOutputQty,
                    unit: "sp");

                var output = ProductionSHelper.BuildStageMaterial(
                    name: $"Thành phẩm hoàn chỉnh {productName}",
                    code: "DAN",
                    estimatedQty: productionOutputQty,
                    actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                    unit: "sp");

                return (
                    inputs,
                    output,
                    BuildNextOutputRef(output, productionOutputQty, output.actual_quantity));
            }

            // Fallback
            AddMainInputFromPrevious(
                fallbackName: $"Bán thành phẩm trước {processName}",
                fallbackCode: "PREV",
                estimatedQty: productionOutputQty,
                unit: "tờ");

            var fallbackOutput = ProductionSHelper.BuildStageMaterial(
                name: $"Bán thành phẩm sau {processName}",
                code: processCode,
                estimatedQty: productionOutputQty,
                actualQty: ActualFromQtyGood(qtyGood, productionOutputQty),
                unit: "tờ");

            return (
                inputs,
                fallbackOutput,
                BuildNextOutputRef(fallbackOutput, productionOutputQty, fallbackOutput.actual_quantity));
        }
        private static bool IsSameUnit(string? source, string target)
        {
            return string.Equals(
                (source ?? "").Trim(),
                target,
                StringComparison.OrdinalIgnoreCase);
        }

        private static StageOutputRef BuildNextOutputRef(
            StageMaterialDto output,
            decimal estimatedQty,
            decimal? actualQty)
        {
            return new StageOutputRef
            {
                Name = output.name ?? "",
                Code = output.code,
                Unit = output.unit,
                EstimatedQuantity = estimatedQty,
                ActualQuantity = actualQty
            };
        }

        private static List<TaskLogDto> LogsByTaskId(List<TaskLogDto> all, int taskId)
        {
            return all
                .Where(x => x.task_id == taskId)
                .OrderBy(x => x.log_time)
                .ToList();
        }

        private static int? GetCurrentSeq(List<TaskRow> tasks)
        {
            var inProg = tasks.FirstOrDefault(x => x.StartTime != null && x.EndTime == null);
            if (inProg?.SeqNum != null) return inProg.SeqNum;

            var next = tasks.FirstOrDefault(x => x.EndTime == null);
            if (next?.SeqNum != null) return next.SeqNum;

            return null;
        }

        private static decimal ComputeProgressByStages(
            List<StepRow> steps,
            int? currentSeq,
            List<TaskRow> tasks)
        {
            var total = steps.Count;
            if (total <= 0) return 0m;

            if (tasks.Count > 0 && tasks.All(x => x.EndTime != null))
                return 100m;

            if (!currentSeq.HasValue) return 0m;

            var idx = steps.FindIndex(s => s.SeqNum == currentSeq.Value);
            if (idx < 0) idx = 0;

            var completedBefore = idx;
            var percent = completedBefore * 100m / total;
            return Math.Round(percent, 1);
        }

        private static void NormalizePaging(ref int page, ref int pageSize)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;
            if (pageSize > 200) pageSize = 200;
        }

        private static string ResolveTaskStageStatus(TaskRow? task)
        {
            if (task == null)
                return "Unassigned";

            if (!string.IsNullOrWhiteSpace(task.Status))
                return task.Status!;

            if (task.EndTime != null)
                return "Finished";

            if (task.StartTime != null)
                return "InProcessing";

            return "Unassigned";
        }

        public async Task<List<MachineScheduleBoardDto>> GetMachineScheduleBoardAsync(
    DateTime from,
    DateTime to,
    CancellationToken ct = default)
        {
            if (to <= from)
                to = from.AddDays(1);

            var machines = await _db.machines
                .AsNoTracking()
                .Where(x => x.is_active)
                .OrderBy(x => x.process_code)
                .ThenBy(x => x.machine_code)
                .Select(x => new
                {
                    x.machine_code,
                    x.process_code,
                    x.process_name,
                    x.quantity,
                    busy_quantity = x.busy_quantity ?? 0,
                    free_quantity = x.free_quantity ?? (x.quantity - (x.busy_quantity ?? 0))
                })
                .ToListAsync(ct);

            var rawRows = await _db.tasks
                .AsNoTracking()
                .Where(t => t.machine != null && t.machine != "")
                .Select(t => new
                {
                    TaskId = t.task_id,
                    ProdId = t.prod_id,
                    ProcessId = t.process_id,
                    SeqNum = t.seq_num,
                    Status = t.status,
                    MachineCode = t.machine,
                    PlannedStart = t.planned_start_time,
                    PlannedEnd = t.planned_end_time,
                    ActualStart = t.start_time,
                    ActualEnd = t.end_time,

                    OrderId = _db.productions
                        .Where(pr => pr.prod_id == t.prod_id)
                        .Select(pr => pr.order_id)
                        .FirstOrDefault(),

                    OrderCode = (
                        from pr in _db.productions
                        join o in _db.orders on pr.order_id equals o.order_id
                        where pr.prod_id == t.prod_id
                        select o.code
                    ).FirstOrDefault(),

                    ProcessCode = _db.product_type_processes
                        .Where(p => p.process_id == t.process_id)
                        .Select(p => p.process_code)
                        .FirstOrDefault(),

                    ProcessName = _db.product_type_processes
                        .Where(p => p.process_id == t.process_id)
                        .Select(p => p.process_name)
                        .FirstOrDefault()
                })
                .ToListAsync(ct);

            var rawSlots = rawRows
                .Select(x =>
                {
                    var start = x.PlannedStart ?? x.ActualStart;
                    var end = x.PlannedEnd
                              ?? x.ActualEnd
                              ?? (start.HasValue ? start.Value.AddHours(1) : (DateTime?)null);

                    return new
                    {
                        x.TaskId,
                        x.ProdId,
                        x.OrderId,
                        x.OrderCode,
                        x.ProcessId,
                        x.ProcessCode,
                        x.ProcessName,
                        x.SeqNum,
                        x.Status,
                        x.MachineCode,
                        Start = start,
                        End = end,
                        x.ActualStart,
                        x.ActualEnd
                    };
                })
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.MachineCode) &&
                    x.Start.HasValue &&
                    x.End.HasValue &&
                    x.Start.Value < to &&
                    x.End.Value > from)
                .Select(x => new MachineScheduleTaskDto
                {
                    task_id = x.TaskId,
                    prod_id = x.ProdId,
                    order_id = x.OrderId,
                    order_code = x.OrderCode,

                    process_id = x.ProcessId,
                    process_code = x.ProcessCode,
                    process_name = !string.IsNullOrWhiteSpace(x.ProcessName) ? x.ProcessName : null,

                    seq_num = x.SeqNum,
                    status = x.Status,

                    machine_code = x.MachineCode!,
                    lane_no = 0,

                    planned_start_time = x.Start,
                    planned_end_time = x.End,

                    actual_start_time = x.ActualStart,
                    actual_end_time = x.ActualEnd
                })
                .OrderBy(x => x.machine_code)
                .ThenBy(x => x.planned_start_time)
                .ThenBy(x => x.planned_end_time)
                .ThenBy(x => x.task_id)
                .ToList();

            var slotsByMachine = rawSlots
                .GroupBy(x => x.machine_code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var result = new List<MachineScheduleBoardDto>();

            foreach (var m in machines)
            {
                slotsByMachine.TryGetValue(m.machine_code, out var slots);
                slots ??= new List<MachineScheduleTaskDto>();

                AssignLaneNumbers(slots, Math.Max(1, m.quantity), from);

                result.Add(new MachineScheduleBoardDto
                {
                    machine_code = m.machine_code,
                    process_code = m.process_code,
                    process_name = m.process_name,
                    quantity = m.quantity,
                    busy_quantity = m.busy_quantity,
                    free_quantity = m.free_quantity,
                    from_time = from,
                    to_time = to,
                    slots = slots
                });
            }

            return result;
        }

        private static void AssignLaneNumbers(
            List<MachineScheduleTaskDto> slots,
            int laneCount,
            DateTime anchor)
        {
            if (slots == null || slots.Count == 0)
                return;

            var laneAvailableAt = Enumerable.Repeat(anchor, laneCount).ToArray();

            foreach (var s in slots
                         .OrderBy(x => x.planned_start_time)
                         .ThenBy(x => x.planned_end_time)
                         .ThenBy(x => x.task_id))
            {
                var start = s.planned_start_time ?? anchor;
                var end = s.planned_end_time ?? start.AddHours(1);

                var bestLane = 0;
                var bestAvailable = laneAvailableAt[0];

                for (var i = 0; i < laneAvailableAt.Length; i++)
                {
                    if (laneAvailableAt[i] <= start)
                    {
                        bestLane = i;
                        bestAvailable = laneAvailableAt[i];
                        break;
                    }

                    if (laneAvailableAt[i] < bestAvailable)
                    {
                        bestLane = i;
                        bestAvailable = laneAvailableAt[i];
                    }
                }

                var actualStart = bestAvailable > start ? bestAvailable : start;
                var actualEnd = end > actualStart ? end : actualStart;

                s.lane_no = bestLane + 1;
                laneAvailableAt[bestLane] = actualEnd;
            }
        }

        public async Task<production?> GetLatestByOrderIdAsync(int orderId, CancellationToken ct = default)
        {
            return await _db.productions
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<SetProductionMethodResponse?> SetProductionMethodAsync(
    SetProductionMethodRequest req,
    CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            if (req.order_id <= 0)
                throw new InvalidOperationException("order_id không hợp lệ.");

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var order = await _db.orders
                    .FirstOrDefaultAsync(x => x.order_id == req.order_id, ct);

                if (order == null)
                    return null;

                var prod = await _db.productions
                    .Where(x => x.order_id == req.order_id)
                    .OrderByDescending(x => x.prod_id)
                    .FirstOrDefaultAsync(ct);

                if (prod == null)
                    throw new InvalidOperationException("Production not found for this order.");

                if (string.Equals(prod.status, "InProcessing", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prod.status, "Delivery", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(prod.status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Không thể thay đổi phương thức sản xuất vì đơn hàng đã bắt đầu hoặc đã hoàn tất sản xuất.");
                }

                var orderReq = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == req.order_id)
                    .OrderByDescending(x => x.order_request_id)
                    .FirstOrDefaultAsync(ct);

                if (orderReq == null)
                    throw new InvalidOperationException("Order request not found for this order.");

                var orderQty = orderReq.quantity ?? 0;

                if (orderQty <= 0)
                {
                    orderQty = await _db.order_items
                        .AsNoTracking()
                        .Where(x => x.order_id == req.order_id)
                        .OrderBy(x => x.item_id)
                        .Select(x => x.quantity)
                        .FirstOrDefaultAsync(ct);
                }

                if (orderQty <= 0)
                    throw new InvalidOperationException("Số lượng đơn hàng không hợp lệ.");

                var method = (req.production_method ?? "").Trim().ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(method))
                {
                    if (req.is_full_process == true)
                        method = "NVL";
                    else if (req.is_full_process == false)
                        method = "SUB";
                }

                if (method is not ("NVL" or "SUB" or "BOTH"))
                    throw new InvalidOperationException("production_method must be NVL | SUB | BOTH.");

                /*
                 * Rollback lựa chọn bán thành phẩm cũ nếu production trước đó đã dùng SUB/BOTH.
                 * Lưu ý: BOTH có is_full_process = null, nên không được check riêng is_full_process == false.
                 */
                if (prod.sub_product_id.HasValue &&
                    prod.sub_product_id.Value > 0 &&
                    prod.sub_product_used_qty > 0)
                {
                    var oldSubProduct = await _db.sub_products
                        .FirstOrDefaultAsync(x => x.id == prod.sub_product_id.Value, ct);

                    if (oldSubProduct != null)
                    {
                        oldSubProduct.quantity += prod.sub_product_used_qty;
                    }

                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                }

                /*
                 * Nếu trước đó SUB đã auto finished một số task bằng bán thành phẩm,
                 * cần rollback trước rồi nếu manager vẫn chọn SUB thì apply lại.
                 */
                await RollbackSubProductFinishedTasksAsync(prod.prod_id, ct);

                prod.mgr_note = string.IsNullOrWhiteSpace(req.mgr_note)
                    ? null
                    : req.mgr_note.Trim();
                prod.production_approval_flow = ProductionApprovalFlowHelper.ManualManager;

                order.is_production_ready = true;
                order.is_enough = true;

                SetProductionMethodResponse response;

                if (method == "NVL")
                {
                    prod.prod_method = "NVL";
                    prod.is_full_process = true;
                    prod.sub_product_id = null;
                    prod.sub_product_used_qty = 0;
                    prod.nvl_qty = orderQty;

                    response = new SetProductionMethodResponse
                    {
                        success = true,
                        order_id = order.order_id,
                        prod_id = prod.prod_id,
                        is_full_process = true,
                        production_method = "NVL",
                        sub_product_id = null,
                        sub_product_used_qty = 0,
                        nvl_qty = orderQty,
                        order_quantity = orderQty,
                        gm_note = prod.gm_note,
                        mgr_note = prod.mgr_note,

                        production_approval_flow = prod.production_approval_flow,
                        is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(prod.production_approval_flow),
                        production_approval_label = ProductionApprovalFlowHelper.Label(prod.production_approval_flow),

                        message = "Đã duyệt sản xuất bằng NVL."
                    };
                }
                else if (method == "SUB")
                {
                    if (!req.sub_id.HasValue || req.sub_id.Value <= 0)
                        throw new InvalidOperationException("Vui lòng truyền sub_id khi chọn SUB.");

                    var selectedSubProduct = await ResolveValidSubProductAsync(
                        req.sub_id.Value,
                        prod,
                        orderReq,
                        orderQty,
                        requireEnoughQty: true,
                        ct);

                    if (selectedSubProduct.quantity < orderQty)
                    {
                        throw new InvalidOperationException(
                            $"Không đủ tồn bán thành phẩm. SubProductId={selectedSubProduct.id}, " +
                            $"tồn={selectedSubProduct.quantity}, cần={orderQty}.");
                    }

                    prod.prod_method = "SUB";
                    prod.is_full_process = false;
                    prod.sub_product_id = selectedSubProduct.id;
                    prod.sub_product_used_qty = orderQty;
                    prod.nvl_qty = 0;

                    await ApplySubProductToExistingTasksAsync(
                        prod,
                        selectedSubProduct,
                        orderQty,
                        ct);

                    response = new SetProductionMethodResponse
                    {
                        success = true,
                        order_id = order.order_id,
                        prod_id = prod.prod_id,
                        is_full_process = false,
                        production_method = "SUB",
                        sub_product_id = selectedSubProduct.id,
                        sub_product_used_qty = orderQty,
                        nvl_qty = 0,
                        order_quantity = orderQty,
                        gm_note = prod.gm_note,
                        mgr_note = prod.mgr_note,

                        production_approval_flow = prod.production_approval_flow,
                        is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(prod.production_approval_flow),
                        production_approval_label = ProductionApprovalFlowHelper.Label(prod.production_approval_flow),

                        sub_product_issue_file = prod.sub_product_issue_file,

                        message = "Đã duyệt sản xuất bằng bán thành phẩm."
                    };
                }
                else if (method == "BOTH")
                {
                    if (!req.sub_id.HasValue || req.sub_id.Value <= 0)
                        throw new InvalidOperationException("Vui lòng truyền sub_id khi chọn BOTH.");

                    var selectedSubProduct = await ResolveValidSubProductAsync(
                        req.sub_id.Value,
                        prod,
                        orderReq,
                        orderQty,
                        requireEnoughQty: false,
                        ct);

                    if (selectedSubProduct.quantity <= 0)
                        throw new InvalidOperationException("Bán thành phẩm không có số lượng để kết hợp.");

                    var subUseQty = Math.Min(selectedSubProduct.quantity, orderQty);
                    var nvlQty = orderQty - subUseQty;

                    if (subUseQty <= 0)
                        throw new InvalidOperationException("Không có số lượng bán thành phẩm hợp lệ để dùng BOTH.");

                    if (nvlQty <= 0)
                        throw new InvalidOperationException("Số lượng bán thành phẩm đã đủ. Vui lòng chọn SUB thay vì BOTH.");

                    prod.prod_method = "BOTH";
                    prod.is_full_process = null;
                    prod.sub_product_id = selectedSubProduct.id;
                    prod.sub_product_used_qty = subUseQty;
                    prod.nvl_qty = nvlQty;

                    response = new SetProductionMethodResponse
                    {
                        success = true,
                        order_id = order.order_id,
                        prod_id = prod.prod_id,
                        is_full_process = null,
                        production_method = "BOTH",
                        sub_product_id = selectedSubProduct.id,
                        sub_product_used_qty = subUseQty,
                        nvl_qty = nvlQty,
                        order_quantity = orderQty,
                        gm_note = prod.gm_note,
                        mgr_note = prod.mgr_note,
                        production_approval_flow = prod.production_approval_flow,
                        is_auto_production_approval = ProductionApprovalFlowHelper.IsAuto(prod.production_approval_flow),
                        production_approval_label = ProductionApprovalFlowHelper.Label(prod.production_approval_flow),

                        message = $"Đã duyệt sản xuất kết hợp. Dùng {subUseQty} bán thành phẩm, sản xuất thêm {nvlQty} bằng NVL."
                    };
                }
                else
                {
                    throw new InvalidOperationException("Unsupported production method.");
                }

                var confirmedEstimate = await LoadAcceptedEstimateOrThrowAsync(
                    orderReq,
                    ct);

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return response;
            });
        }

        private sealed class ReserveBomItem
        {
            public int MaterialId { get; init; }

            public string? MaterialCode { get; init; }

            public string? MaterialName { get; init; }

            public string? MaterialType { get; init; }

            public string? Unit { get; init; }

            public string ProcessCode { get; init; } = "";

            public decimal RequiredQty { get; init; }
        }


        private static bool IsPlateReserveMaterial(string? materialCode, string? materialName)
        {
            var code = NormalizeReserveMaterialCode(materialCode);
            var name = NormalizeReserveMaterialCode(materialName);

            return code == "PLATE" || name == "PLATE";
        }

        private async Task ReleasePreviousProductionMaterialReserveAsync(
            int prodId,
            CancellationToken ct)
        {
            var reserveRefDoc = $"PROD-RESERVE-{prodId}";
            var releaseRefDoc = $"PROD-RESERVE-RELEASE-{prodId}";

            var moves = await _db.stock_moves
                .Where(x =>
                    x.ref_doc == reserveRefDoc ||
                    x.ref_doc == releaseRefDoc)
                .ToListAsync(ct);

            if (moves.Count == 0)
                return;

            var netByMaterial = moves
                .GroupBy(x => x.material_id)
                .Select(g => new
                {
                    material_id = g.Key,
                    reserved_qty = g
                        .Where(x => x.type == "OUT" && x.ref_doc == reserveRefDoc)
                        .Sum(x => x.qty),
                    released_qty = g
                        .Where(x => x.type == "IN" && x.ref_doc == releaseRefDoc)
                        .Sum(x => x.qty)
                })
                .Select(x => new
                {
                    x.material_id,
                    net_qty = x.reserved_qty - x.released_qty
                })
                .Where(x => x.net_qty > 0)
                .ToList();

            if (netByMaterial.Count == 0)
                return;

            var materialIds = netByMaterial
                .Select(x => x.material_id)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            var now = AppTime.NowVnUnspecified();

            foreach (var item in netByMaterial)
            {
                if (!materials.TryGetValue((int)item.material_id, out var mat))
                    continue;

                mat.stock_qty = (mat.stock_qty ?? 0m) + item.net_qty;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = item.material_id,
                    type = "IN",
                    qty = item.net_qty,
                    ref_doc = releaseRefDoc,
                    user_id = null,
                    move_date = now,
                    note = $"Rollback reserve NVL cho production {prodId} trước khi xác nhận lại method."
                }, ct);
            }
        }

        private static string NormReserveText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var s = raw.Trim().ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Z0-9]+", "_");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_").Trim('_');

            return s;
        }

        private static string NormalizeReserveMaterialCode(string? raw)
        {
            var s = NormReserveText(raw);

            return s switch
            {
                "KEM_THO" => "PLATE",
                "BAN_KEM_THO" => "PLATE",
                "BAN_KEM" => "PLATE",
                "BAN_KEM_IN" => "PLATE",
                "PLATE_INPUT" => "PLATE",

                "MUC" => "INK",
                "MUC_IN" => "INK",
                "MUC_TONG_HOP" => "INK",
                "INK_TYPES" => "INK",

                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "PHU_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                "MANG_12_MIC" => "MANG_12MIC",

                "MOUNTING_GLUE" => "KEO_BOI",
                "KEO_BOI" => "KEO_BOI",

                _ => s
            };
        }

        private static HashSet<string> ParseReserveProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToUpperInvariant().Replace(" ", "_").Replace("-", "_"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsPlateReserveMaterial(
            string? materialCode,
            string? materialName,
            string? materialType)
        {
            var code = NormalizeReserveMaterialCode(materialCode);
            var name = NormalizeReserveMaterialCode(materialName);
            var type = NormReserveText(materialType);

            return code == "PLATE" ||
                   name == "PLATE" ||
                   type == "KEM";
        }

        private static string ResolveMaterialProcessCodeForReserve(
            string? materialCode,
            string? materialName,
            string? materialType)
        {
            var code = NormalizeReserveMaterialCode(materialCode);
            var name = NormalizeReserveMaterialCode(materialName);
            var type = NormReserveText(materialType);

            /*
             * Database hiện có:
             * - PLATE / type Kẽm
             * - GIAY_* / type Giấy
             * - INK, MUC_* / type Mực
             * - KEO_PHU_* / type Keo phủ
             * - MANG_* / type Màng
             * - SONG_* / type Sóng
             * - KEO_BOI / type Keo bồi
             * - KEO_DAN / type Keo dán
             */

            if (code == "PLATE" || type == "KEM")
                return "RALO";

            if (code.StartsWith("GIAY_") || type == "GIAY")
                return "CAT";

            if (code == "INK" || code.StartsWith("MUC_") || type == "MUC")
                return "IN";

            if (code.StartsWith("KEO_PHU_") || type == "KEO_PHU")
                return "PHU";

            if (code.StartsWith("MANG_") || type == "MANG")
                return "CAN";

            if (code.StartsWith("SONG_") || type == "SONG")
                return "BOI";

            if (code == "KEO_BOI" || type == "KEO_BOI")
                return "BOI";

            if (code == "KEO_DAN" || type == "KEO_DAN")
                return "DAN";

            /*
             * Fallback theo tên nếu type/code không chuẩn.
             */
            if (name.Contains("GIAY"))
                return "CAT";

            if (name.Contains("MUC"))
                return "IN";

            if (name.Contains("MANG"))
                return "CAN";

            if (name.Contains("SONG") || name.Contains("BOI"))
                return "BOI";

            if (name.Contains("DAN"))
                return "DAN";

            return "";
        }

        private static bool ShouldScaleBomLineForBoth(
            string materialProcessCode,
            string? materialCode,
            string? materialName,
            string? materialType,
            HashSet<string> subProcessCodes)
        {
            if (string.IsNullOrWhiteSpace(materialProcessCode))
                return false;

            /*
             * Kẽm không scale theo số lượng sản phẩm.
             * Dù BOTH chỉ sản xuất thêm một phần, số bản kẽm vẫn theo thiết kế/số màu.
             */
            if (IsPlateReserveMaterial(materialCode, materialName, materialType))
                return false;

            /*
             * Chỉ scale NVL nếu NVL đó thuộc công đoạn đã được bán thành phẩm cover.
             *
             * Ví dụ:
             * sub_product_process = RALO,CAT,IN
             * => giấy/mực scale theo nvl_qty/orderQty.
             * => keo phủ, màng, sóng, keo bồi không scale vì vẫn chạy cho toàn bộ sản lượng.
             */
            return subProcessCodes.Contains(materialProcessCode);
        }

        private async Task<List<ReserveBomItem>> BuildReserveBomItemsAsync(
    production prod,
    string method,
    int orderQty,
    CancellationToken ct)
        {
            if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                throw new InvalidOperationException("Production chưa có order_id.");

            var orderId = prod.order_id.Value;
            var normalizedMethod = (method ?? "").Trim().ToUpperInvariant();

            if (normalizedMethod == "SUB")
                return new List<ReserveBomItem>();

            if (orderQty <= 0)
                throw new InvalidOperationException("Số lượng đơn hàng không hợp lệ khi reserve NVL.");

            decimal nvlRatio = 1m;
            HashSet<string> subProcessCodes = new(StringComparer.OrdinalIgnoreCase);

            if (normalizedMethod == "BOTH")
            {
                if (prod.nvl_qty <= 0)
                    throw new InvalidOperationException("nvl_qty không hợp lệ khi reserve NVL cho BOTH.");

                nvlRatio = Math.Clamp(prod.nvl_qty / (decimal)orderQty, 0m, 1m);

                if (nvlRatio <= 0m)
                    return new List<ReserveBomItem>();

                if (prod.sub_product_id.HasValue && prod.sub_product_id.Value > 0)
                {
                    var subProcessCsv = await _db.sub_products
                        .AsNoTracking()
                        .Where(x => x.id == prod.sub_product_id.Value)
                        .Select(x => x.product_process)
                        .FirstOrDefaultAsync(ct);

                    subProcessCodes = ParseReserveProcessCodes(subProcessCsv);
                }

                if (subProcessCodes.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Không xác định được product_process của bán thành phẩm để reserve NVL cho BOTH.");
                }
            }

            var bomLines = await (
                from oi in _db.order_items.AsNoTracking()

                join b in _db.boms.AsNoTracking()
                    on oi.item_id equals b.order_item_id

                join m0 in _db.materials.AsNoTracking()
                    on b.material_id equals (int?)m0.material_id into mj
                from m in mj.DefaultIfEmpty()

                where oi.order_id == orderId

                select new
                {
                    oi.item_id,
                    order_qty = oi.quantity,

                    b.material_id,
                    b.material_code,
                    b.material_name,
                    b.unit,
                    b.qty_total,
                    b.qty_per_product,
                    b.wastage_percent,

                    resolved_material_id = m != null ? (int?)m.material_id : null,
                    resolved_material_code = m != null ? m.code : null,
                    resolved_material_name = m != null ? m.name : null,
                    resolved_material_type = m != null ? m.type : null,
                    resolved_material_unit = m != null ? m.unit : null
                }
            ).ToListAsync(ct);

            if (bomLines.Count == 0)
                throw new InvalidOperationException("No BOM found for this order. Cannot reserve materials.");

            var invalidBomLines = bomLines
                .Where(x =>
                    !x.material_id.HasValue ||
                    x.material_id.Value <= 0 ||
                    !x.resolved_material_id.HasValue)
                .ToList();

            if (invalidBomLines.Count > 0)
            {
                throw new InvalidOperationException(
                    "Một số BOM line chưa map đúng material_id trong bảng materials. " +
                    $"item_ids={string.Join(",", invalidBomLines.Select(x => x.item_id).Distinct())}");
            }

            var result = new List<ReserveBomItem>();

            foreach (var line in bomLines)
            {
                var materialCode = line.resolved_material_code ?? line.material_code;
                var materialName = line.resolved_material_name ?? line.material_name;
                var materialType = line.resolved_material_type;
                var materialUnit = line.resolved_material_unit ?? line.unit;

                var processCode = ResolveMaterialProcessCodeForReserve(
                    materialCode,
                    materialName,
                    materialType);

                decimal lineQty;

                if (line.qty_total > 0m)
                {
                    lineQty = (decimal)line.qty_total;
                }
                else
                {
                    var lineOrderQty = line.order_qty <= 0 ? 1 : line.order_qty;
                    var qtyPerProduct = line.qty_per_product ?? 0m;
                    var wastageFactor = 1m + ((line.wastage_percent ?? 0m) / 100m);

                    lineQty = lineOrderQty * qtyPerProduct * wastageFactor;
                }

                if (lineQty <= 0m)
                    continue;

                if (normalizedMethod == "BOTH" &&
                    ShouldScaleBomLineForBoth(
                        processCode,
                        materialCode,
                        materialName,
                        materialType,
                        subProcessCodes))
                {
                    lineQty *= nvlRatio;
                }

                result.Add(new ReserveBomItem
                {
                    MaterialId = line.material_id!.Value,
                    MaterialCode = materialCode,
                    MaterialName = materialName,
                    MaterialType = materialType,
                    Unit = materialUnit,
                    ProcessCode = processCode,
                    RequiredQty = Math.Round(lineQty, 4)
                });
            }

            return result
                .GroupBy(x => x.MaterialId)
                .Select(g =>
                {
                    var first = g.First();

                    return new ReserveBomItem
                    {
                        MaterialId = g.Key,
                        MaterialCode = first.MaterialCode,
                        MaterialName = first.MaterialName,
                        MaterialType = first.MaterialType,
                        Unit = first.Unit,
                        ProcessCode = string.Join(
                            ",",
                            g.Select(x => x.ProcessCode)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase)),
                        RequiredQty = Math.Round(g.Sum(x => x.RequiredQty), 4)
                    };
                })
                .Where(x => x.RequiredQty > 0)
                .ToList();
        }

        private async Task ReserveMaterialsForConfirmedProductionMethodAsync(
            production prod,
            order_request orderReq,
            cost_estimate confirmedEstimate,
            string method,
            int orderQty,
            CancellationToken ct)
        {
            if (prod.prod_id <= 0)
                throw new InvalidOperationException("Production chưa có prod_id.");

            if (!prod.order_id.HasValue || prod.order_id.Value <= 0)
                throw new InvalidOperationException("Production chưa có order_id.");

            var normalizedMethod = (method ?? "").Trim().ToUpperInvariant();

            if (normalizedMethod is not ("NVL" or "SUB" or "BOTH"))
                throw new InvalidOperationException($"Method sản xuất không hợp lệ: {method}");

            /*
             * Chống reserve trùng hoặc đổi method.
             * Nếu production trước đó đã reserve NVL thì hoàn lại phần net đang giữ.
             */
            await ReleasePreviousProductionMaterialReserveAsync(
                prod.prod_id,
                ct);

            /*
             * SUB chỉ dùng bán thành phẩm, không reserve NVL.
             */
            if (normalizedMethod == "SUB")
                return;

            var reserveItems = await BuildReserveBomItemsAsync(
                prod,
                normalizedMethod,
                orderQty,
                ct);

            if (reserveItems.Count == 0)
                return;

            var materialIds = reserveItems
                .Select(x => x.MaterialId)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            foreach (var item in reserveItems)
            {
                if (!materials.TryGetValue(item.MaterialId, out var mat))
                {
                    throw new InvalidOperationException(
                        $"Material not found. material_id={item.MaterialId}, code={item.MaterialCode}");
                }

                var stockQty = mat.stock_qty ?? 0m;

                if (stockQty < item.RequiredQty)
                {
                    throw new InvalidOperationException(
                        $"Không đủ tồn kho NVL '{mat.name}' ({mat.code}). " +
                        $"Tồn={stockQty}, cần reserve={item.RequiredQty} {mat.unit}.");
                }
            }

            var now = AppTime.NowVnUnspecified();
            var refDoc = $"PROD-RESERVE-{prod.prod_id}";

            foreach (var item in reserveItems)
            {
                var mat = materials[item.MaterialId];

                mat.stock_qty = (mat.stock_qty ?? 0m) - item.RequiredQty;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = item.MaterialId,
                    type = "OUT",
                    qty = item.RequiredQty,
                    ref_doc = refDoc,
                    user_id = prod.manager_id,
                    move_date = now,
                    note =
                        $"Reserve NVL khi confirm method {normalizedMethod}. " +
                        $"prod_id={prod.prod_id}, order_id={prod.order_id}, " +
                        $"estimate_id={confirmedEstimate.estimate_id}"
                }, ct);
            }
        }

        private async Task<cost_estimate> LoadAcceptedEstimateOrThrowAsync(
    order_request orderReq,
    CancellationToken ct)
        {
            cost_estimate? estimate = null;

            if (orderReq.accepted_estimate_id.HasValue &&
                orderReq.accepted_estimate_id.Value > 0)
            {
                estimate = await _db.cost_estimates
                    .FirstOrDefaultAsync(x =>
                        x.estimate_id == orderReq.accepted_estimate_id.Value &&
                        x.order_request_id == orderReq.order_request_id,
                        ct);
            }

            estimate ??= await _db.cost_estimates
                .Where(x => x.order_request_id == orderReq.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            if (estimate == null)
                throw new InvalidOperationException("Không tìm thấy cost_estimate để reserve NVL.");

            return estimate;
        }

        private async Task<sub_product> ResolveValidSubProductAsync(
    int subId,
    production prod,
    order_request orderReq,
    int orderQty,
    bool requireEnoughQty,
    CancellationToken ct)
        {
            if (!prod.product_type_id.HasValue || prod.product_type_id.Value <= 0)
                throw new InvalidOperationException("Production chưa có product_type_id.");

            if (!orderReq.print_width_mm.HasValue || orderReq.print_width_mm.Value <= 0)
                throw new InvalidOperationException("Order request chưa có print_width_mm.");

            if (!orderReq.print_length_mm.HasValue || orderReq.print_length_mm.Value <= 0)
                throw new InvalidOperationException("Order request chưa có print_length_mm.");

            var orderId = orderReq.order_id ?? prod.order_id;

            if (!orderId.HasValue || orderId.Value <= 0)
                throw new InvalidOperationException("Không xác định được order_id để kiểm tra bán thành phẩm.");

            var selectedSubProduct = await _db.sub_products
                .Include(x => x.product_type)
                .FirstOrDefaultAsync(x => x.id == subId, ct);

            if (selectedSubProduct == null)
                throw new InvalidOperationException($"Không tìm thấy bán thành phẩm có id = {subId}.");

            if (selectedSubProduct.is_active != true)
                throw new InvalidOperationException("Bán thành phẩm đã chọn đang không hoạt động.");

            if (selectedSubProduct.is_imported != true)
                throw new InvalidOperationException("Bán thành phẩm đã chọn chưa được nhập kho.");

            if (selectedSubProduct.quantity <= 0)
                throw new InvalidOperationException("Bán thành phẩm không còn số lượng tồn.");

            if (selectedSubProduct.product_type_id != prod.product_type_id.Value)
                throw new InvalidOperationException("Bán thành phẩm đã chọn không cùng loại sản phẩm với production.");

            if (selectedSubProduct.width != orderReq.print_width_mm.Value)
            {
                throw new InvalidOperationException(
                    $"Bán thành phẩm không đúng chiều rộng. " +
                    $"Yêu cầu: {orderReq.print_width_mm.Value}, thực tế: {selectedSubProduct.width}.");
            }

            if (selectedSubProduct.length != orderReq.print_length_mm.Value)
            {
                throw new InvalidOperationException(
                    $"Bán thành phẩm không đúng chiều dài. " +
                    $"Yêu cầu: {orderReq.print_length_mm.Value}, thực tế: {selectedSubProduct.length}.");
            }

            if (requireEnoughQty && selectedSubProduct.quantity < orderQty)
            {
                throw new InvalidOperationException(
                    $"Số lượng bán thành phẩm không đủ. " +
                    $"Cần: {orderQty}, hiện có: {selectedSubProduct.quantity}.");
            }

            if (!requireEnoughQty && selectedSubProduct.quantity <= 0)
                throw new InvalidOperationException("Bán thành phẩm không còn số lượng để kết hợp.");

            var orderRouteCsv = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId.Value)
                .OrderBy(x => x.item_id)
                .Select(x => x.production_process)
                .FirstOrDefaultAsync(ct);

            if (!IsSubPathUsableForOrderRouteRepo(
                    selectedSubProduct.product_process,
                    orderRouteCsv))
            {
                throw new InvalidOperationException(
                    $"Path bán thành phẩm không phù hợp với route đơn hàng. " +
                    $"SubPath={selectedSubProduct.product_process}, OrderRoute={orderRouteCsv}.");
            }

            var expected = await BuildExpectedSubProductStageMaterialSignatureAsync(
                orderId.Value,
                selectedSubProduct.product_process,
                ct);

            if (expected == null)
                throw new InvalidOperationException("Không build được NVL kỳ vọng cho đơn hàng.");

            if (!IsMaterialMatchedForProductionSubRepo(
                    selectedSubProduct,
                    expected))
            {
                var subStages = ParseRouteForSubCheck(selectedSubProduct.product_process);

                var requiredChecks = new List<string> { "giấy" };

                if (HasStageForSubCheck(subStages, "PHU"))
                    requiredChecks.Add("keo phủ");

                if (HasStageForSubCheck(subStages, "CAN", "CAN_MANG"))
                    requiredChecks.Add("màng cán");

                if (HasStageForSubCheck(subStages, "BOI"))
                    requiredChecks.Add("sóng");

                throw new InvalidOperationException(
                    "Bán thành phẩm không hợp lệ vì khác NVL theo stage đã cover. " +
                    $"Các điều kiện cần check: {string.Join(", ", requiredChecks)}. " +
                    $"SubPath={selectedSubProduct.product_process}, " +
                    $"SubPaper={selectedSubProduct.paper_material_code}, ExpectedPaper={expected.paper_material_code}, " +
                    $"SubCoating={selectedSubProduct.coating_material_code}, ExpectedCoating={expected.coating_material_code}, " +
                    $"SubLamination={selectedSubProduct.lamination_material_code}, ExpectedLamination={expected.lamination_material_code}, " +
                    $"SubWave={selectedSubProduct.wave_material_code}, ExpectedWave={expected.wave_material_code}.");
            }

            return selectedSubProduct;
        }

        private async Task RollbackSubProductFinishedTasksAsync(
    int prodId,
    CancellationToken ct)
        {
            var tasks = await _db.tasks
                .Where(x =>
                    x.prod_id == prodId &&
                    x.is_taken_sub_product == true)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return;

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs
                .Where(x =>
                    x.task_id.HasValue &&
                    taskIds.Contains(x.task_id.Value) &&
                    x.action_type == "Finished" &&
                    x.scanned_code != null &&
                    x.scanned_code.StartsWith("SUB_PRODUCT-"))
                .ToListAsync(ct);

            _db.task_logs.RemoveRange(logs);

            foreach (var t in tasks)
            {
                t.status = "Unassigned";
                t.start_time = null;
                t.end_time = null;
                t.reason = null;
                t.is_taken_sub_product = false;
            }
        }

        private static string NormProcessCodeForSubProduct(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static HashSet<string> ParseSubProductProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(
                    new[] { ',', ';', '|', '/', '\\' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(NormProcessCodeForSubProduct)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ProductionQtyContext
        {
            public int order_qty { get; init; } = 1;
            public int sheets_total { get; init; } = 1;
            public int sheets_required { get; init; } = 1;
            public int n_up { get; init; } = 1;
            public int number_of_plates { get; init; } = 1;
        }

        private async Task<ProductionQtyContext> GetProductionQtyContextAsync(
            int orderId,
            CancellationToken ct)
        {
            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            var orderQty = req?.quantity ?? 0;

            if (orderQty <= 0)
            {
                orderQty = await _db.order_items
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderBy(x => x.item_id)
                    .Select(x => x.quantity)
                    .FirstOrDefaultAsync(ct);
            }

            if (orderQty <= 0)
                orderQty = 1;

            cost_estimate? est = null;

            if (req != null)
            {
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
            }

            var sheetsRequired = Math.Max(est?.sheets_required ?? 0, 0);
            var sheetsTotal = Math.Max(est?.sheets_total ?? 0, sheetsRequired);
            var nUp = est?.n_up > 0 ? est.n_up : 1;
            var numberOfPlates = req?.number_of_plates ?? 1;

            if (sheetsRequired <= 0)
                sheetsRequired = Math.Max(1, (int)Math.Ceiling(orderQty / (decimal)nUp));

            if (sheetsTotal <= 0)
                sheetsTotal = sheetsRequired;

            if (sheetsTotal <= 0)
                sheetsTotal = 1;

            if (numberOfPlates <= 0)
                numberOfPlates = 1;

            return new ProductionQtyContext
            {
                order_qty = orderQty,
                sheets_required = sheetsRequired,
                sheets_total = sheetsTotal,
                n_up = nUp,
                number_of_plates = numberOfPlates
            };
        }

        private static int ResolveQtyGoodForSubProductTask(
            string? processCode,
            int stageIndex,
            IReadOnlyList<string?> routeProcessCodes,
            ProductionQtyContext ctx)
        {
            return StageQuantityHelper.GetProductionOutputCap(
                currentCode: processCode,
                currentStageIndex: stageIndex,
                routeProcessCodes: routeProcessCodes,
                sheetsTotal: ctx.sheets_total,
                nUp: ctx.n_up,
                numberOfPlates: ctx.number_of_plates);
        }

        /// <summary>
        /// Khi production đã có task rồi, chọn sub_product sẽ tự Finished các task từ đầu route
        /// </summary>
        private async Task ApplySubProductToExistingTasksAsync(
            production prod,
            sub_product selectedSubProduct,
            int orderQty,
            CancellationToken ct)
        {
            if (prod.is_full_process != false)
                return;

            if (string.IsNullOrWhiteSpace(selectedSubProduct.product_process))
                return;

            if (!prod.order_id.HasValue)
                return;

            var selectedCodes = ParseSubProductProcessCodes(selectedSubProduct.product_process);
            if (selectedCodes.Count == 0)
                return;

            var tasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == prod.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            if (tasks.Count == 0)
                return;

            var maxCompletedSeq = tasks
                .Where(x => selectedCodes.Contains(NormProcessCodeForSubProduct(x.process?.process_code)))
                .Select(x => x.seq_num)
                .Where(x => x.HasValue)
                .Select(x => (int?)x!.Value)
                .Max();

            if (!maxCompletedSeq.HasValue)
                return;

            var now = AppTime.NowVnUnspecified();
            var reason = "Bán thành phẩm đã có sẵn trong kho";

            var routeCodes = tasks
                .Select(x => (string?)x.process?.process_code)
                .ToList();

            var qtyCtx = await GetProductionQtyContextAsync(prod.order_id.Value, ct);

            for (var i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];

                if (!t.seq_num.HasValue || t.seq_num.Value > maxCompletedSeq.Value)
                    continue;

                if (string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    continue;

                t.status = "Finished";
                t.start_time ??= now;
                t.end_time = now;
                t.reason = reason;
                t.is_taken_sub_product = true;

                var alreadyHasLog = await _db.task_logs
                    .AsNoTracking()
                    .AnyAsync(x =>
                        x.task_id == t.task_id &&
                        x.action_type == "Finished",
                        ct);

                if (!alreadyHasLog)
                {
                    var qtyGood = ResolveQtyGoodForSubProductTask(
                        t.process?.process_code,
                        i,
                        routeCodes,
                        qtyCtx);

                    await _db.task_logs.AddAsync(new task_log
                    {
                        task_id = t.task_id,
                        scanned_code = $"SUB_PRODUCT-{selectedSubProduct.id}",
                        action_type = "Finished",
                        qty_good = qtyGood,
                        log_time = now,
                        scanned_by_user_id = null,
                        material_usage_json = null
                    }, ct);
                }
            }
        }

        private static string RemoveDiacriticsForMaterial(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();

            foreach (var ch in normalized)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static string NormalizeMaterialCodeForDetail(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var s = RemoveDiacriticsForMaterial(raw)
                .Trim()
                .ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Z0-9]+", "_");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"_+", "_").Trim('_');

            return s switch
            {
                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",

                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",

                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "PHU_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                _ => s
            };
        }

        private static bool IsNoCoatingType(string? coatingType)
        {
            var s = NormalizeMaterialCodeForDetail(coatingType);

            return string.IsNullOrWhiteSpace(s)
                   || s == "NONE"
                   || s == "NO"
                   || s == "NO_COATING"
                   || s == "KHONG"
                   || s == "KHONG_PHU"
                   || s == "KHONG_COATING";
        }

        private static string? ResolveCoatingMaterialCodeForDetail(string? coatingType)
        {
            if (IsNoCoatingType(coatingType))
                return null;

            var code = NormalizeMaterialCodeForDetail(coatingType);

            return string.IsNullOrWhiteSpace(code) ? null : code;
        }

        private async Task<material?> ResolveCoatingMaterialForDetailAsync(
            cost_estimate est,
            CancellationToken ct = default)
        {
            if (est.coating_glue_weight_kg <= 0m)
                return null;

            if (IsNoCoatingType(est.coating_type))
                return null;

            var code = ResolveCoatingMaterialCodeForDetail(est.coating_type);
            var displayName = ProductionFlowHelper.ResolveCoatingDisplayName(est.coating_type);

            var aliases = new List<string?> { code, est.coating_type, displayName }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeMaterialCodeForDetail)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return null;

            var allMaterials = await _db.materials
                .AsNoTracking()
                .ToListAsync(ct);

            return allMaterials.FirstOrDefault(m =>
                aliases.Contains(NormalizeMaterialCodeForDetail(m.code)) ||
                aliases.Contains(NormalizeMaterialCodeForDetail(m.name)));
        }

        public async Task<ImportReceiveSourceDto?> GetImportReceiveSourceByOrderIdAsync(
    int orderId,
    CancellationToken ct = default)
        {
            var sources = await GetImportReceiveSourcesByOrderIdAsync(orderId, ct);

            return sources
                .OrderByDescending(x => x.prod_id)
                .FirstOrDefault();
        }

        public async Task<bool> SaveImportReceivePathAsync(int prodId, string path, CancellationToken ct = default)
        {
            var prod = await _db.productions.FirstOrDefaultAsync(x => x.prod_id == prodId, ct);
            if (prod == null) return false;

            prod.import_recieve_path = path;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<List<ImportReceiveSourceDto>> GetImportReceiveSourcesByOrderIdAsync(
    int orderId,
    CancellationToken ct = default)
        {
            if (orderId <= 0)
                return new List<ImportReceiveSourceDto>();

            var order = await _db.orders
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

            if (order == null)
                return new List<ImportReceiveSourceDto>();

            // 1. Production riêng gắn trực tiếp với order
            var directProdIds = await _db.productions
                .AsNoTracking()
                .Where(x =>
                    x.order_id == orderId &&
                    (
                        x.prod_kind == null ||
                        x.prod_kind.ToUpper() != "GROUP"
                    ))
                .Select(x => x.prod_id)
                .ToListAsync(ct);

            // 2. Production được gắn ở orders.production_id
            // Nhưng vẫn phải check không phải GROUP
            var orderProductionIds = new List<int>();

            if (order.production_id.HasValue && order.production_id.Value > 0)
            {
                var isGroupProduction = await _db.productions
                    .AsNoTracking()
                    .AnyAsync(x =>
                        x.prod_id == order.production_id.Value &&
                        x.prod_kind != null &&
                        x.prod_kind.ToUpper() == "GROUP",
                        ct);

                if (!isGroupProduction)
                    orderProductionIds.Add(order.production_id.Value);
            }

            // 3. Nếu order có nằm trong prod_orders:
            // po.prod_id là production ghép GROUP => KHÔNG lấy
            // po.single_prod_id là production riêng của order nếu có => CÓ THỂ lấy
            var singleProdIdsFromProdOrders = await _db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.order_id == orderId &&
                    x.single_prod_id != null &&
                    x.single_prod_id.Value > 0 &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .Select(x => x.single_prod_id!.Value)
                .Distinct()
                .ToListAsync(ct);

            var allProdIds = directProdIds
                .Concat(orderProductionIds)
                .Concat(singleProdIdsFromProdOrders)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (allProdIds.Count == 0)
                return new List<ImportReceiveSourceDto>();

            // 4. Lấy production cuối cùng và loại bỏ GROUP lần nữa để chắc chắn
            var productions = await _db.productions
                .AsNoTracking()
                .Where(x =>
                    allProdIds.Contains(x.prod_id) &&
                    (
                        x.prod_kind == null ||
                        x.prod_kind.ToUpper() != "GROUP"
                    ))
                .OrderBy(x => x.prod_id)
                .ToListAsync(ct);

            if (productions.Count == 0)
                return new List<ImportReceiveSourceDto>();

            var items = await (
                from oi in _db.order_items.AsNoTracking()

                join pt in _db.product_types.AsNoTracking()
                    on oi.product_type_id equals pt.product_type_id into ptJoin
                from pt in ptJoin.DefaultIfEmpty()

                where oi.order_id == orderId

                orderby oi.item_id

                select new ImportReceiveItemDto
                {
                    item_id = oi.item_id,
                    product_name = oi.product_name,
                    quantity = oi.quantity,
                    packaging_standard = pt != null ? pt.packaging_standard : null
                }
            ).ToListAsync(ct);

            var result = productions.Select(prod => new ImportReceiveSourceDto
            {
                prod_id = prod.prod_id,
                order_id = order.order_id,
                order_code = order.code ?? "",
                items = items
            }).ToList();

            return result;
        }

        private static BothStageQuantityContext ResolveBothStageQuantityContext(
    ProductionDetailDto detail,
    string? processCode,
    int currentStageIndex,
    IReadOnlyList<string?> routeProcessCodes,
    int fullSheetQty,
    int fullOutputQty)
        {
            var isBoth = string.Equals(
                detail.production_method,
                "BOTH",
                StringComparison.OrdinalIgnoreCase);

            if (!isBoth)
            {
                return new BothStageQuantityContext
                {
                    stage_sheet_qty = fullSheetQty,
                    stage_output_qty = fullOutputQty,
                    is_both = false,
                    is_stage_covered_by_sub = false,
                    nvl_ratio = 1m
                };
            }

            var orderQty = detail.quantity <= 0 ? 1 : detail.quantity;
            var nvlQty = detail.nvl_qty > 0
                ? detail.nvl_qty
                : Math.Max(orderQty - detail.sub_product_used_qty, 0);

            if (nvlQty <= 0)
                nvlQty = orderQty;

            var nvlRatio = Math.Clamp((decimal)nvlQty / orderQty, 0m, 1m);

            var subCodes = ParseSelectedProcessCodes(detail.sub_product_process);

            var subLastIndex = -1;

            for (var i = 0; i < routeProcessCodes.Count; i++)
            {
                var code = NormalizeProcessCode(routeProcessCodes[i]);

                if (subCodes.Contains(code))
                    subLastIndex = i;
            }

            var currentCode = NormalizeProcessCode(processCode);

            var isRalo = currentCode == "RALO" || currentCode == "RA_LO";

            // Nếu sub_product đã đi tới công đoạn X, thì các công đoạn từ đầu tới X chỉ cần sản xuất phần thiếu bằng NVL.
            var isCoveredBySub =
                subLastIndex >= 0 &&
                currentStageIndex <= subLastIndex;

            if (!isCoveredBySub)
            {
                // quay về tổng số lượng full.
                return new BothStageQuantityContext
                {
                    stage_sheet_qty = fullSheetQty,
                    stage_output_qty = fullOutputQty,
                    is_both = true,
                    is_stage_covered_by_sub = false,
                    nvl_ratio = nvlRatio
                };
            }

            if (isRalo)
            {
                return new BothStageQuantityContext
                {
                    stage_sheet_qty = fullSheetQty,
                    stage_output_qty = fullOutputQty,
                    is_both = true,
                    is_stage_covered_by_sub = true,
                    nvl_ratio = nvlRatio
                };
            }

            var scaledSheetQty = Math.Max(
                1,
                (int)Math.Ceiling(fullSheetQty * nvlRatio));

            var scaledOutputQty = Math.Max(
                1,
                (int)Math.Ceiling(fullOutputQty * nvlRatio));

            return new BothStageQuantityContext
            {
                stage_sheet_qty = scaledSheetQty,
                stage_output_qty = scaledOutputQty,
                is_both = true,
                is_stage_covered_by_sub = true,
                nvl_ratio = nvlRatio
            };
        }

        private async Task<ProductionDetailSourceItem?> LoadProductionDetailSourceItemAsync(
    int orderId,
    CancellationToken ct)
        {
            return await _db.order_items
                .AsNoTracking()
                .Where(i => i.order_id == orderId)
                .OrderBy(i => i.item_id)
                .Select(i => new ProductionDetailSourceItem
                {
                    item_id = i.item_id,
                    product_name = i.product_name,
                    quantity = i.quantity,
                    production_process = i.production_process,
                    length_mm = EF.Property<int?>(i, "length_mm"),
                    width_mm = EF.Property<int?>(i, "width_mm"),
                    height_mm = EF.Property<int?>(i, "height_mm"),
                    est_ink_weight_kg = i.est_ink_weight_kg
                })
                .FirstOrDefaultAsync(ct);
        }

        private static string NormProdKind(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant();
        }

        private static bool IsGroupProductionKind(string? value)
        {
            return string.Equals(
                NormProdKind(value),
                "GROUP",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSplitProductionKind(string? value)
        {
            return string.Equals(
                NormProdKind(value),
                "SPLIT",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldConsumeMaterialsWhenStart(production prod)
        {
            /*
             * SINGLE: giữ logic cũ, start thì xuất NVL theo BOM.
             *
             * GROUP: task group đang input_mode MANUAL, NVL được báo cáo/xuất khi finish task group.
             * SPLIT: BE/DUT/DAN là phần tách sau group, không xuất lại NVL đầu vào của order.
             *
             * Nếu để GROUP/SPLIT gọi ConsumeMaterialsOnProductionStartAsync thì:
             * - GROUP sẽ lỗi vì order_id = null.
             * - SPLIT có thể xuất NVL trùng với SINGLE.
             */
            return !IsGroupProductionKind(prod.prod_kind) &&
                   !IsSplitProductionKind(prod.prod_kind);
        }

        private async Task SyncDirectProductionOrderToInProcessingAsync(
            production prod,
            CancellationToken ct)
        {
            if (!prod.order_id.HasValue)
                return;

            var order = await _db.orders
                .FirstOrDefaultAsync(o => o.order_id == prod.order_id.Value, ct);

            if (order == null)
                return;

            if (!string.Equals(order.status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(order.status, "Delivery", StringComparison.OrdinalIgnoreCase))
            {
                order.status = "InProcessing";
            }
        }

        private async Task SyncGroupMemberOrdersToInProcessingAsync(
            int groupProdId,
            CancellationToken ct)
        {
            var orderIds = await _db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.prod_id == groupProdId &&
                    x.status == "Active")
                .Select(x => x.order_id)
                .Distinct()
                .ToListAsync(ct);

            if (orderIds.Count == 0)
                return;

            var orders = await _db.orders
                .Where(x => orderIds.Contains(x.order_id))
                .ToListAsync(ct);

            foreach (var order in orders)
            {
                if (string.Equals(order.status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(order.status, "Delivery", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                order.status = "InProcessing";
            }
        }

        private async Task<int?> ResolveGroupRepresentativeOrderIdAsync(
    int groupProdId,
    CancellationToken ct)
        {
            return await _db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.prod_id == groupProdId &&
                    x.status == "Active")
                .OrderBy(x => x.id)
                .Select(x => (int?)x.order_id)
                .FirstOrDefaultAsync(ct);
        }

        private sealed class ProductionDetailSourceItem
        {
            public int item_id { get; set; }

            public string? product_name { get; set; }

            public int quantity { get; set; }

            public string? production_process { get; set; }

            public int? length_mm { get; set; }

            public int? width_mm { get; set; }

            public int? height_mm { get; set; }

            public decimal? est_ink_weight_kg { get; set; }
        }

        private sealed class RepoSubProductStageMaterialSignature
        {
            public string? paper_material_code { get; set; }

            public string? wave_material_code { get; set; }

            public string? coating_material_code { get; set; }

            public string? lamination_material_code { get; set; }
        }

        private static readonly string[] RepoFullRouteOrder =
        {
    "RALO", "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN"
};

        private static string NormForSubCheck(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var s = RemoveDiacriticsForSubCheck(value)
                .Trim()
                .ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = s.Replace("-", "_").Replace(" ", "_");

            while (s.Contains("__"))
                s = s.Replace("__", "_");

            return s.Trim('_');
        }

        private static string NormalizeMaterialCodeForSubCheck(string? value)
        {
            var s = NormForSubCheck(value);

            return s switch
            {
                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",

                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",

                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "PHU_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                "MANG_12_MIC" => "MANG_12MIC",
                "MANG_12MIC" => "MANG_12MIC",

                "MOUNTING_GLUE" => "KEO_BOI",
                "KEO_BOI" => "KEO_BOI",

                _ => s
            };
        }

        private static string RemoveDiacriticsForSubCheck(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();

            foreach (var ch in normalized)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static List<string> ParseRouteForSubCheck(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormForSubCheck)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(RouteIndexForSubCheck)
                .ToList();
        }

        private static int RouteIndexForSubCheck(string? processCode)
        {
            var code = NormForSubCheck(processCode);

            var idx = Array.FindIndex(
                RepoFullRouteOrder,
                x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

            return idx < 0 ? 999 : idx;
        }

        private static bool IsSubPathUsableForOrderRouteRepo(
            string? subProductProcess,
            string? orderRouteCsv)
        {
            var subCodes = ParseRouteForSubCheck(subProductProcess);
            var orderCodes = ParseRouteForSubCheck(orderRouteCsv);

            if (subCodes.Count == 0 || orderCodes.Count == 0)
                return false;

            foreach (var code in subCodes)
            {
                if (!orderCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                    return false;
            }

            var subLastIndex = subCodes
                .Select(x => orderCodes.FindIndex(y =>
                    string.Equals(y, x, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .DefaultIfEmpty(-1)
                .Max();

            var expectedPrefix = orderCodes.Take(subLastIndex + 1).ToList();

            return expectedPrefix.SequenceEqual(
                subCodes,
                StringComparer.OrdinalIgnoreCase);
        }

        private async Task<RepoSubProductStageMaterialSignature?> BuildExpectedSubProductStageMaterialSignatureAsync(
            int orderId,
            string? subProductProcess,
            CancellationToken ct)
        {
            var est = await LoadAcceptedEstimateByOrderIdForSubCheckAsync(
                orderId,
                ct);

            if (est == null)
                return null;

            var codes = ParseRouteForSubCheck(subProductProcess);

            var paperCode = NormalizeMaterialCodeForSubCheck(
                !string.IsNullOrWhiteSpace(est.paper_code)
                    ? est.paper_code
                    : est.paper_alternative);

            string? waveCode = null;
            if (HasStageForSubCheck(codes, "BOI"))
            {
                waveCode = NormalizeMaterialCodeForSubCheck(
                    EstimateMaterialAlternativeHelper.ResolveWaveType(
                        est.wave_alternative,
                        est.wave_type));
            }

            string? coatingCode = null;
            if (HasStageForSubCheck(codes, "PHU"))
            {
                coatingCode = ResolveCoatingMaterialCodeForSubCheck(est);
            }

            string? laminationCode = null;
            if (HasStageForSubCheck(codes, "CAN", "CAN_MANG"))
            {
                laminationCode = await ResolveLaminationMaterialCodeForSubCheckAsync(
                    est,
                    ct);
            }

            return new RepoSubProductStageMaterialSignature
            {
                paper_material_code = NullIfEmptyForSubCheck(paperCode),
                wave_material_code = NullIfEmptyForSubCheck(waveCode),
                coating_material_code = NullIfEmptyForSubCheck(coatingCode),
                lamination_material_code = NullIfEmptyForSubCheck(laminationCode)
            };
        }

        private async Task<cost_estimate?> LoadAcceptedEstimateByOrderIdForSubCheckAsync(
            int orderId,
            CancellationToken ct)
        {
            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            if (req == null)
                return null;

            cost_estimate? est = null;

            if (req.accepted_estimate_id.HasValue &&
                req.accepted_estimate_id.Value > 0)
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

            return est;
        }

        private static string? ResolveCoatingMaterialCodeForSubCheck(cost_estimate est)
        {
            if (!string.IsNullOrWhiteSpace(est.coating_material_code))
                return NormalizeMaterialCodeForSubCheck(est.coating_material_code);

            return NormalizeMaterialCodeForSubCheck(est.coating_type);
        }

        private async Task<string?> ResolveLaminationMaterialCodeForSubCheckAsync(
            cost_estimate est,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(est.lamination_material_code))
                return NormalizeMaterialCodeForSubCheck(est.lamination_material_code);

            if (!string.IsNullOrWhiteSpace(est.lamination_material_name))
                return NormalizeMaterialCodeForSubCheck(est.lamination_material_name);

            if (est.lamination_material_id.HasValue &&
                est.lamination_material_id.Value > 0)
            {
                var code = await _db.materials
                    .AsNoTracking()
                    .Where(x => x.material_id == est.lamination_material_id.Value)
                    .Select(x => x.code)
                    .FirstOrDefaultAsync(ct);

                return NormalizeMaterialCodeForSubCheck(code);
            }

            return null;
        }

        private static bool IsMaterialMatchedForProductionSubRepo(
            sub_product sub,
            RepoSubProductStageMaterialSignature expected)
        {
            var subStages = ParseRouteForSubCheck(sub.product_process);

            if (subStages.Count == 0)
                return false;

            /*
             * Giấy luôn phải check.
             */
            if (!SameRequiredMaterialForSubCheck(
                    sub.paper_material_code,
                    expected.paper_material_code))
            {
                return false;
            }

            /*
             * Có PHU mới check keo phủ.
             */
            if (HasStageForSubCheck(subStages, "PHU"))
            {
                if (!SameRequiredMaterialForSubCheck(
                        sub.coating_material_code,
                        expected.coating_material_code))
                {
                    return false;
                }
            }

            /*
             * Có CAN/CAN_MANG mới check màng.
             */
            if (HasStageForSubCheck(subStages, "CAN", "CAN_MANG"))
            {
                if (!SameRequiredMaterialForSubCheck(
                        sub.lamination_material_code,
                        expected.lamination_material_code))
                {
                    return false;
                }
            }

            /*
             * Có BOI mới check sóng.
             */
            if (HasStageForSubCheck(subStages, "BOI"))
            {
                if (!SameRequiredMaterialForSubCheck(
                        sub.wave_material_code,
                        expected.wave_material_code))
                {
                    return false;
                }
            }

            /*
             * Không check unit_cost_to_stage.
             * Không check material_signature.
             */
            return true;
        }

        private static bool HasStageForSubCheck(
            IReadOnlyList<string> stages,
            params string[] codes)
        {
            if (stages == null || stages.Count == 0)
                return false;

            var set = codes
                .Select(NormForSubCheck)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return stages.Any(x => set.Contains(NormForSubCheck(x)));
        }

        private static bool SameRequiredMaterialForSubCheck(
            string? actual,
            string? expected)
        {
            var a = NormalizeMaterialCodeForSubCheck(actual);
            var b = NormalizeMaterialCodeForSubCheck(expected);

            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string? NullIfEmptyForSubCheck(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }

        private static TaskRow? ResolveCurrentTaskForCanStart(
    List<TaskRow> visibleTasks)
        {
            if (visibleTasks == null || visibleTasks.Count == 0)
                return null;

            /*
             * Current task là task đầu tiên chưa Finished theo thứ tự seq.
             * Đồng bộ với stage_statuses.is_current hiện đang hiển thị ở FE.
             */
            return visibleTasks
                .Where(x => !IsFinishedStatus(x.Status, x.EndTime))
                .OrderBy(x => x.SeqNum ?? int.MaxValue)
                .ThenBy(x => x.TaskId)
                .FirstOrDefault();
        }

        private async Task<(bool? can_start, string? message)> ResolveCanStartForProductionCardAsync(
    BaseRow row,
    TaskRow? currentTask,
    bool? isProductionReady,
    CancellationToken ct)
        {
            if (currentTask == null)
            {
                if (string.Equals(row.production_status, "Pending", StringComparison.OrdinalIgnoreCase))
                    return (false, "Production chưa được GM xác nhận lập lịch nên chưa có task để bắt đầu.");

                return (false, "Production chưa có task.");
            }

            if (IsFinishedStatus(currentTask.Status, currentTask.EndTime))
                return (false, "Công đoạn hiện tại đã Finished.");

            if (StatusEquals(currentTask.Status, "GroupedWaiting"))
                return (false, "Task này đã được ghép vào production chung, không thể chạy riêng.");

            if (isProductionReady != true)
                return (false, "Order/production chưa được xác nhận sẵn sàng sản xuất.");

            if (!string.IsNullOrWhiteSpace(currentTask.Status) &&
                !StatusEquals(currentTask.Status, "Unassigned") &&
                !StatusEquals(currentTask.Status, "Ready"))
            {
                return (false, $"Task đang ở trạng thái {currentTask.Status}, không thể bắt đầu.");
            }

            /*
             * FIX QUAN TRỌNG:
             * Không được return true ngay khi task Ready.
             * Dù Ready vẫn phải check dependency vì có thể data cũ/bug cũ đã set Ready sai.
             */
            var dep = await CheckTaskCanStartForCardAsync(
                currentTask.TaskId,
                ct);

            if (!dep.can_start)
                return (false, dep.message);

            if (StatusEquals(currentTask.Status, "Ready"))
                return (true, "Task đã Ready, có thể báo cáo sản xuất.");

            return (true, "OK");
        }

        private async Task<(bool can_start, string message)> CheckTaskCanStartForCardAsync(
    int taskId,
    CancellationToken ct)
        {
            var currentTask = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (currentTask == null)
                return (false, $"Task {taskId} không tồn tại.");

            if (!currentTask.prod_id.HasValue)
                return (false, $"Task {taskId} chưa gắn production.");

            if (!currentTask.seq_num.HasValue)
                return (false, $"Task {taskId} chưa có seq_num.");

            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == currentTask.prod_id.Value, ct);

            if (prod == null)
                return (false, $"Không tìm thấy production của task {taskId}.");

            var currentCode = NormCanStartCode(currentTask.process?.process_code);
            if (string.IsNullOrWhiteSpace(currentCode))
                return (false, $"Task {taskId} chưa có process_code.");

            /*
             * 1. Check pipeline nội bộ trong cùng production.
             * Ví dụ:
             * - GROUP: CAN phải đợi PHU Finished.
             * - SPLIT: DUT phải đợi BE Finished.
             * - SINGLE: công đoạn sau phải đợi công đoạn trước trong cùng production.
             */
            var internalCheck = await CheckInternalPipelineForCanStartAsync(
                currentTask,
                currentCode,
                ct);

            if (!internalCheck.can_start)
                return internalCheck;

            /*
             * 2. Check dependency theo route thật của order.
             * Đây là phần fix case order 124/127:
             * nếu production sau đang ở PHU/CAN/BE... thì phải nhìn lại order route
             * và tìm đúng công đoạn trước của order, dù công đoạn đó nằm ở SINGLE/GROUP/SPLIT khác.
             */
            var routeCheck = await CheckOrderRouteDependencyForCanStartAsync(
                prod,
                currentTask,
                currentCode,
                ct);

            if (!routeCheck.can_start)
                return routeCheck;

            return (true, "OK");
        }

        private async Task<(bool can_start, string message)> CheckInternalPipelineForCanStartAsync(
    task currentTask,
    string currentCode,
    CancellationToken ct)
        {
            if (!currentTask.prod_id.HasValue || !currentTask.seq_num.HasValue)
                return (false, $"Task {currentTask.task_id} thiếu prod_id hoặc seq_num.");

            var currentProdId = currentTask.prod_id.Value;
            var currentSeq = currentTask.seq_num.Value;

            /*
             * RALO/CAT/IN có thể là initial parallel.
             */
            var isInitialParallel = ProductionFlowHelper.IsInitialParallel(currentCode);

            if (isInitialParallel)
                return (true, "OK");

            /*
             * FIX rõ ràng:
             * Chỉ check các task đứng trước trong cùng prod_id.
             */
            var previousUnfinished = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x =>
                    x.prod_id == currentProdId &&
                    x.task_id != currentTask.task_id &&
                    x.seq_num.HasValue &&
                    x.seq_num.Value < currentSeq &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "FINISHED"
                    ))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .FirstOrDefaultAsync(ct);

            if (previousUnfinished == null)
                return (true, "OK");

            var prevCode = previousUnfinished.process?.process_code ?? previousUnfinished.name ?? "";
            var prevStatus = ShowCanStartStatus(previousUnfinished.status);

            return (
                false,
                $"Công đoạn {currentCode} chưa được start vì công đoạn trước đó {prevCode} trong cùng production chưa Finished. " +
                $"current_prod_id={currentProdId}, previous_task_id={previousUnfinished.task_id}, status={prevStatus}.");
        }

        private async Task<(bool can_start, string message)> CheckOrderRouteDependencyForCanStartAsync(
    production currentProd,
    task currentTask,
    string currentCode,
    CancellationToken ct)
        {
            var orderIds = await ResolveOrderIdsOfProductionForCanStartAsync(
                currentProd,
                ct);

            if (orderIds.Count == 0)
                return (true, "OK");

            var issues = new List<string>();

            foreach (var orderId in orderIds)
            {
                var route = await GetOrderRouteForCanStartAsync(orderId, ct);

                if (route.Count == 0)
                    continue;

                var previousCode = ResolvePreviousProcessCodeForCanStart(
                    route,
                    currentCode);

                /*
                 * currentCode là công đoạn đầu tiên của route order.
                 * Ví dụ RALO thì không cần check previous.
                 */
                if (string.IsNullOrWhiteSpace(previousCode))
                    continue;

                var previous = await FindPreviousStageForOrderForCanStartAsync(
    orderId: orderId,
    previousCode: previousCode,
    currentTaskId: currentTask.task_id,
    currentProdId: currentProd.prod_id,
    ct: ct);

                if (previous == null)
                {
                    issues.Add(
                        $"Order {orderId}: công đoạn {currentCode} chưa được start vì không tìm thấy công đoạn trước đó {previousCode}.");
                    continue;
                }

                if (!IsFinishedStatus(previous.status, previous.end_time))
                {
                    issues.Add(
                        $"Order {orderId}: công đoạn {currentCode} chưa được start vì công đoạn trước đó {previousCode} chưa Finished. " +
                        $"previous_task_id={previous.task_id}, status={ShowCanStartStatus(previous.status)}.");
                    continue;
                }

                /*
                 * Nếu previous là GROUP thì ngoài Finished còn phải có task_qtys phân bổ cho order.
                 * Nếu không có qty phân bổ, SPLIT/production sau chưa được start.
                 */
                if (previous.is_group_task)
                {
                    var hasAllocatedQty = await _db.task_qtys
                        .AsNoTracking()
                        .AnyAsync(x =>
                            x.group_task_id == previous.task_id &&
                            x.order_id == orderId &&
                            x.process_code != null &&
                            x.process_code.Trim().ToUpper() == previousCode &&
                            x.qty_good > 0,
                            ct);

                    if (!hasAllocatedQty)
                    {
                        issues.Add(
                            $"Order {orderId}: công đoạn ghép trước đó {previousCode} đã Finished nhưng chưa có sản lượng phân bổ task_qtys.");
                    }
                }
            }

            if (issues.Count > 0)
                return (false, string.Join(" | ", issues));

            return (true, "OK");
        }

        private sealed class PreviousStageForCanStart
        {
            public int task_id { get; set; }
            public int? prod_id { get; set; }
            public string? prod_kind { get; set; }
            public string? status { get; set; }
            public DateTime? end_time { get; set; }
            public bool is_group_task { get; set; }
        }

        private async Task<List<int>> ResolveOrderIdsOfProductionForCanStartAsync(
            production prod,
            CancellationToken ct)
        {
            if (prod.order_id.HasValue && prod.order_id.Value > 0)
                return new List<int> { prod.order_id.Value };

            /*
             * GROUP production không có order_id nên lấy từ prod_orders.
             */
            var orderIds = await _db.prod_orders
                .AsNoTracking()
                .Where(x =>
                    x.prod_id == prod.prod_id &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .Select(x => x.order_id)
                .Distinct()
                .ToListAsync(ct);

            return orderIds;
        }

        private async Task<List<string>> GetOrderRouteForCanStartAsync(
            int orderId,
            CancellationToken ct)
        {
            var csv = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => x.production_process)
                .FirstOrDefaultAsync(ct);

            return ParseCanStartRoute(csv);
        }

        private static List<string> ParseCanStartRoute(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormCanStartCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? ResolvePreviousProcessCodeForCanStart(
            List<string> route,
            string currentCode)
        {
            currentCode = NormCanStartCode(currentCode);

            var idx = route.FindIndex(x =>
                string.Equals(
                    NormCanStartCode(x),
                    currentCode,
                    StringComparison.OrdinalIgnoreCase));

            if (idx <= 0)
                return null;

            return route[idx - 1];
        }

        private async Task<PreviousStageForCanStart?> FindPreviousStageForOrderForCanStartAsync(
    int orderId,
    string previousCode,
    int currentTaskId,
    int currentProdId,
    CancellationToken ct)
        {
            previousCode = NormCanStartCode(previousCode);

            if (orderId <= 0 || string.IsNullOrWhiteSpace(previousCode))
                return null;

            /*
             * 1. Nếu công đoạn trước cũng là GROUP task,
             * tìm theo task_links của chính order đó.
             *
             * Ví dụ CAN group phải đợi PHU group.
             */
            var groupPrevious = await (
                from tl in _db.task_links.AsNoTracking()

                join gt in _db.tasks.AsNoTracking()
                    on tl.group_task_id equals gt.task_id

                join gp in _db.productions.AsNoTracking()
                    on gt.prod_id equals gp.prod_id

                where tl.order_id == orderId
                      && tl.group_task_id != currentTaskId
                      && tl.process_code != null
                      && tl.process_code.Trim().ToUpper() == previousCode
                      && (
                            tl.status == null ||
                            tl.status.ToUpper() != "CANCELLED"
                         )
                      && (
                            gp.status == null ||
                            gp.status.ToUpper() != "CANCELLED"
                         )

                orderby gt.seq_num descending, gt.task_id descending

                select new PreviousStageForCanStart
                {
                    task_id = gt.task_id,
                    prod_id = gt.prod_id,
                    prod_kind = gp.prod_kind,
                    status = gt.status,
                    end_time = gt.end_time,
                    is_group_task = true
                }
            ).FirstOrDefaultAsync(ct);

            if (groupPrevious != null)
                return groupPrevious;

            /*
             * 2. FIX CHÍNH:
             * Nếu current task là GROUP task, phải ưu tiên tìm previous task
             * trong đúng single_prod_id đã link với order đó.
             *
             * Ví dụ:
             * current group PHU task_id=102.
             * Order A có single_prod_id=4.
             * previousCode=IN.
             * => phải tìm IN trong production 4, không tìm lung tung theo order_id.
             */
            var linkedSingleProdId = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.group_task_id == currentTaskId &&
                    x.order_id == orderId &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .Select(x => (int?)x.single_prod_id)
                .FirstOrDefaultAsync(ct);

            if (linkedSingleProdId.HasValue && linkedSingleProdId.Value > 0)
            {
                var linkedPrevious = await FindDirectPreviousStageByProdIdForCanStartAsync(
                    prodId: linkedSingleProdId.Value,
                    previousCode: previousCode,
                    currentTaskId: currentTaskId,
                    ct: ct);

                if (linkedPrevious != null)
                    return linkedPrevious;
            }

            /*
             * 3. Fallback:
             * Tìm theo order_id như logic cũ,
             * nhưng không lấy chính current production nếu current production cũng có order_id.
             */
            var directPrevious = await (
                from t in _db.tasks.AsNoTracking()

                join p in _db.productions.AsNoTracking()
                    on t.prod_id equals p.prod_id

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where p.order_id == orderId
                      && p.prod_id != currentProdId
                      && t.task_id != currentTaskId
                      && pp != null
                      && pp.process_code != null
                      && pp.process_code.Trim().ToUpper() == previousCode
                      && (
                            p.status == null ||
                            p.status.ToUpper() != "CANCELLED"
                         )

                orderby t.seq_num descending, t.task_id descending

                select new PreviousStageForCanStart
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    prod_kind = p.prod_kind,
                    status = t.status,
                    end_time = t.end_time,
                    is_group_task = false
                }
            ).FirstOrDefaultAsync(ct);

            return directPrevious;
        }

        private async Task<PreviousStageForCanStart?> FindDirectPreviousStageByProdIdForCanStartAsync(
    int prodId,
    string previousCode,
    int currentTaskId,
    CancellationToken ct)
        {
            previousCode = NormCanStartCode(previousCode);

            if (prodId <= 0 || string.IsNullOrWhiteSpace(previousCode))
                return null;

            return await (
                from t in _db.tasks.AsNoTracking()

                join p in _db.productions.AsNoTracking()
                    on t.prod_id equals p.prod_id

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where p.prod_id == prodId
                      && t.task_id != currentTaskId
                      && pp != null
                      && pp.process_code != null
                      && pp.process_code.Trim().ToUpper() == previousCode
                      && (
                            p.status == null ||
                            p.status.ToUpper() != "CANCELLED"
                         )

                orderby t.seq_num descending, t.task_id descending

                select new PreviousStageForCanStart
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    prod_kind = p.prod_kind,
                    status = t.status,
                    end_time = t.end_time,
                    is_group_task = false
                }
            ).FirstOrDefaultAsync(ct);
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

        private static string NormCanStartCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static string ShowCanStartStatus(string? status)
        {
            return string.IsNullOrWhiteSpace(status)
                ? "(null)"
                : status.Trim();
        }

        private sealed class CanGroupCandidateRow
        {
            public int prod_id { get; set; }

            public int order_id { get; set; }

            public int product_type_id { get; set; }

            public string prod_method { get; set; } = "";

            public DateTime? delivery_date { get; set; }

            public string? production_process { get; set; }

            public List<string> route_codes { get; set; } = new();

            /*
             * Chỉ lấy các công đoạn Dept2 còn task thật, chưa Ready/Finished,
             * để tránh báo can_group=true cho production đã được bắt đầu hoặc đã bị ghép.
             */
            public List<string> available_group_process_codes { get; set; } = new();

            public cost_estimate? estimate { get; set; }
        }

        private async Task<HashSet<int>> ResolveCanGroupProductionIdsForGetAllProductionAsync(
            CancellationToken ct)
        {
            var result = new HashSet<int>();

            var today = AppTime.NowVnUnspecified().Date;
            var minDeliveryDate = today.AddDays(7);

            /*
             * Đồng bộ với GroupProductionService.GetCandidatesAsync:
             * - production riêng SINGLE
             * - order Scheduled
             * - layout_confirmed
             * - is_production_ready
             * - delivery_date >= hôm nay + 7 ngày
             * - không nằm trong production GROUP active
             *
             * Bổ sung cho get-all-production:
             * - production.status phải Scheduled
             * - đã có task
             * - task Dept2 còn Unassigned/null thì mới báo có thể ghép
             */
            var seedRows = await (
                from o in _db.orders.AsNoTracking()

                join pr in _db.productions.AsNoTracking()
                    on o.order_id equals pr.order_id

                where pr.prod_kind == "SINGLE"
                      && pr.status == "Scheduled"
                      && o.status == "Scheduled"
                      && o.layout_confirmed
                      && o.is_production_ready
                      && o.delivery_date != null
                      && o.delivery_date >= minDeliveryDate
                      && _db.tasks.Any(t => t.prod_id == pr.prod_id)
                      && !_db.prod_orders.Any(po =>
                            po.order_id == o.order_id &&
                            po.status == "Active" &&
                            _db.productions.Any(g =>
                                g.prod_id == po.prod_id &&
                                g.prod_kind == "GROUP" &&
                                g.status != "Cancelled" &&
                                g.status != "Completed"))

                select new
                {
                    pr.prod_id,
                    o.order_id,
                    product_type_id = pr.product_type_id,
                    prod_method = pr.prod_method,
                    o.delivery_date,

                    item = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new
                        {
                            i.product_type_id,
                            i.production_process
                        })
                        .FirstOrDefault()
                }
            ).ToListAsync(ct);

            if (seedRows.Count < 2)
                return result;

            var seedProdIds = seedRows
                .Select(x => x.prod_id)
                .Distinct()
                .ToList();

            var taskCodes = await (
                    from t in _db.tasks.AsNoTracking()

                    join pp0 in _db.product_type_processes.AsNoTracking()
                        on t.process_id equals pp0.process_id into ppj
                    from pp in ppj.DefaultIfEmpty()

                    where t.prod_id.HasValue &&
                          seedProdIds.Contains(t.prod_id.Value)

                    select new
                    {
                        task_id = t.task_id,
                        prod_id = t.prod_id!.Value,
                        process_code = pp != null ? pp.process_code : null,
                        t.status,
                        t.start_time,
                        t.end_time
                    }
                ).ToListAsync(ct);

            var allTaskIds = taskCodes
    .Select(x => x.task_id)
    .Distinct()
    .ToList();

            var taskIdsWithLogs = await _db.task_logs
                .AsNoTracking()
                .Where(x =>
                    x.task_id.HasValue &&
                    allTaskIds.Contains(x.task_id.Value))
                .Select(x => x.task_id!.Value)
                .Distinct()
                .ToListAsync(ct);

            var logTaskSet = taskIdsWithLogs.ToHashSet();

            var startedProdIds = taskCodes
                .Where(x => IsTaskStartedForCanGroupSuggestion(
                    x.status,
                    x.start_time,
                    x.end_time,
                    logTaskSet.Contains(x.task_id)))
                .Select(x => x.prod_id)
                .Distinct()
                .ToHashSet();

            var availableDept2ByProdId = taskCodes
                .Where(x => IsDept2ForCanGroup(x.process_code))
                .Where(x =>
                    x.end_time == null &&
                    x.start_time == null &&
                    (
                        x.status == null ||
                        x.status.ToUpper() == "UNASSIGNED"
                    ))
                .GroupBy(x => x.prod_id)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Select(x => NormCanGroupCode(x.process_code))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

            var candidates = new List<CanGroupCandidateRow>();

            foreach (var row in seedRows)
            {
                if (startedProdIds.Contains(row.prod_id))
                    continue;

                if (row.item == null)
                    continue;

                var productTypeId =
                    row.product_type_id ??
                    row.item.product_type_id;

                if (!productTypeId.HasValue || productTypeId.Value <= 0)
                    continue;

                var method = NormalizeProductionMethodForCanGroup(row.prod_method);

                if (string.IsNullOrWhiteSpace(method))
                    continue;

                var routeCodes = ParseProcessCodesForCanGroup(row.item.production_process);

                if (routeCodes.Count == 0)
                    continue;

                if (!availableDept2ByProdId.TryGetValue(row.prod_id, out var availableDept2Codes) ||
                    availableDept2Codes.Count == 0)
                {
                    continue;
                }

                /*
                 * Chỉ xét công đoạn vừa nằm trong route của order,
                 * vừa còn task Dept2 thật trong production.
                 */
                var groupableCodes = availableDept2Codes
                    .Where(code => routeCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (groupableCodes.Count == 0)
                    continue;

                var estimate = await LoadAcceptedEstimateByOrderIdForCanGroupAsync(
                    row.order_id,
                    ct);

                candidates.Add(new CanGroupCandidateRow
                {
                    prod_id = row.prod_id,
                    order_id = row.order_id,
                    product_type_id = productTypeId.Value,
                    prod_method = method,
                    delivery_date = row.delivery_date,
                    production_process = row.item.production_process,
                    route_codes = routeCodes,
                    available_group_process_codes = groupableCodes,
                    estimate = estimate
                });
            }

            if (candidates.Count < 2)
                return result;

            /*
             * Đồng bộ với SuggestAsync:
             * group theo product_type_id + prod_method.
             */
            foreach (var productMethodGroup in candidates
                .GroupBy(x => new
                {
                    x.product_type_id,
                    x.prod_method
                })
                .Where(g => g.Count() >= 2))
            {
                var rowsOfOneTypeAndMethod = productMethodGroup
                    .OrderBy(x => x.delivery_date)
                    .ThenBy(x => x.order_id)
                    .ToList();

                /*
                 * Đồng bộ với BuildAutoDept2Suggestions:
                 * chỉ auto suggest Dept2: PHU/CAN/CAN_MANG/BOI.
                 */
                var possibleDept2Codes = rowsOfOneTypeAndMethod
                    .SelectMany(x => x.available_group_process_codes)
                    .Where(IsDept2ForCanGroup)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndexForCanGroup)
                    .ToList();

                foreach (var processCode in possibleDept2Codes)
                {
                    var membersWithProcess = rowsOfOneTypeAndMethod
                        .Where(x => x.available_group_process_codes.Contains(
                            processCode,
                            StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (membersWithProcess.Count < 2)
                        continue;

                    if (RequiresSameMaterialKeyForCanGroup(processCode))
                    {
                        var keyedMembers = new List<(string key, CanGroupCandidateRow row)>();

                        foreach (var member in membersWithProcess)
                        {
                            var key = ResolveCanGroupPlanKey(
                                processCode,
                                member);

                            keyedMembers.Add((key, member));
                        }

                        var validMaterialGroups = keyedMembers
                            .GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() >= 2)
                            .ToList();

                        foreach (var materialGroup in validMaterialGroups)
                        {
                            foreach (var member in materialGroup)
                            {
                                result.Add(member.row.prod_id);
                            }
                        }
                    }
                    else
                    {
                        foreach (var member in membersWithProcess)
                        {
                            result.Add(member.prod_id);
                        }
                    }
                }
            }

            return result;
        }

        private static bool IsTaskStartedForCanGroup(
    string? status,
    DateTime? startTime,
    DateTime? endTime,
    bool hasLog)
        {
            if (startTime != null || endTime != null || hasLog)
                return true;

            var s = (status ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(s))
                return false;

            /*
             * Đồng bộ với GroupProductionService:
             * Chỉ Unassigned/null là chưa bắt đầu.
             * Ready cũng xem là đã bắt đầu vì task đã được mở/giữ máy/cho phép báo cáo.
             */
            return s != "UNASSIGNED";
        }

        private async Task<HashSet<int>> LoadSingleProdIdsHavingStartedTaskForCanGroupAsync(
            List<int> singleProdIds,
            CancellationToken ct)
        {
            singleProdIds = singleProdIds
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (singleProdIds.Count == 0)
                return new HashSet<int>();

            var taskRows = await _db.tasks
                .AsNoTracking()
                .Where(x =>
                    x.prod_id.HasValue &&
                    singleProdIds.Contains(x.prod_id.Value))
                .Select(x => new
                {
                    x.task_id,
                    prod_id = x.prod_id!.Value,
                    x.status,
                    x.start_time,
                    x.end_time
                })
                .ToListAsync(ct);

            if (taskRows.Count == 0)
                return new HashSet<int>();

            var taskIds = taskRows
                .Select(x => x.task_id)
                .Distinct()
                .ToList();

            var taskIdsWithLogs = await _db.task_logs
                .AsNoTracking()
                .Where(x =>
                    x.task_id.HasValue &&
                    taskIds.Contains(x.task_id.Value))
                .Select(x => x.task_id!.Value)
                .Distinct()
                .ToListAsync(ct);

            var logSet = taskIdsWithLogs.ToHashSet();

            return taskRows
                .Where(x => IsTaskStartedForCanGroup(
                    x.status,
                    x.start_time,
                    x.end_time,
                    logSet.Contains(x.task_id)))
                .Select(x => x.prod_id)
                .Distinct()
                .ToHashSet();
        }

        private static bool IsTaskStartedForCanGroupSuggestion(
    string? status,
    DateTime? startTime,
    DateTime? endTime,
    bool hasLog)
        {
            if (startTime != null || endTime != null || hasLog)
                return true;

            var s = (status ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(s))
                return false;

            /*
             * Đồng bộ với GroupProductionService:
             * chỉ Unassigned/null được xem là chưa bắt đầu.
             */
            return s != "UNASSIGNED";
        }

        private static readonly string[] CanGroupDept2Codes =
{
    "PHU", "CAN", "CAN_MANG", "BOI"
};

        private static readonly string[] CanGroupFullRouteOrder =
        {
    "RALO", "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN"
};

        private static string? NormalizeProductionMethodForCanGroup(string? method)
        {
            var value = (method ?? "")
                .Trim()
                .ToUpperInvariant();

            return value switch
            {
                "SUB" => "SUB",
                "NVL" => "NVL",
                "BOTH" => "BOTH",
                _ => null
            };
        }

        private static List<string> ParseProcessCodesForCanGroup(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormCanGroupCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndexForCanGroup)
                .ToList();
        }

        private static string NormCanGroupCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return RemoveDiacriticsForCanGroup(value)
                .Trim()
                .ToUpperInvariant()
                .Replace("Đ", "D")
                .Replace("-", "_")
                .Replace(" ", "_")
                .Trim('_');
        }

        private static string RemoveDiacriticsForCanGroup(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();

            foreach (var ch in normalized)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static bool IsDept2ForCanGroup(string? processCode)
        {
            var code = NormCanGroupCode(processCode);

            return CanGroupDept2Codes.Contains(
                code,
                StringComparer.OrdinalIgnoreCase);
        }

        private static int FullRouteIndexForCanGroup(string? processCode)
        {
            var code = NormCanGroupCode(processCode);

            var idx = Array.FindIndex(
                CanGroupFullRouteOrder,
                x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

            return idx < 0 ? 999 : idx;
        }

        private static bool RequiresSameMaterialKeyForCanGroup(string? processCode)
        {
            var code = NormCanGroupCode(processCode);

            /*
             * Đồng bộ với GroupProductionService.RequiresSameMaterialKey:
             * PHU check keo phủ
             * CAN/CAN_MANG check màng
             * BOI check sóng
             */
            return code is "PHU" or "CAN" or "CAN_MANG" or "BOI";
        }

        private static string SafeKeyForCanGroup(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "NULL"
                : NormCanGroupCode(value);
        }

        private string ResolveCanGroupPlanKey(
            string processCode,
            CanGroupCandidateRow row)
        {
            var method = NormalizeProductionMethodForCanGroup(row.prod_method) ?? "INVALID";
            var materialKey = ResolveMaterialGroupKeyForCanGroup(processCode, row);

            return $"METHOD={method}|{materialKey}";
        }

        private string ResolveMaterialGroupKeyForCanGroup(
            string processCode,
            CanGroupCandidateRow row)
        {
            var code = NormCanGroupCode(processCode);

            if (code == "PHU")
            {
                var coating = ResolveCoatingMaterialCodeForCanGroup(row.estimate);
                return $"PHU:COATING={SafeKeyForCanGroup(coating)}";
            }

            if (code is "CAN" or "CAN_MANG")
            {
                var lamination =
                    !string.IsNullOrWhiteSpace(row.estimate?.lamination_material_code)
                        ? row.estimate!.lamination_material_code
                        : !string.IsNullOrWhiteSpace(row.estimate?.lamination_material_name)
                            ? row.estimate!.lamination_material_name
                            : row.estimate?.lamination_material_id?.ToString();

                return $"{code}:LAMINATION={SafeKeyForCanGroup(lamination)}";
            }

            if (code == "BOI")
            {
                var wave = EstimateMaterialAlternativeHelper.ResolveWaveType(
                    row.estimate?.wave_alternative,
                    row.estimate?.wave_type);

                return $"BOI:WAVE={SafeKeyForCanGroup(wave)}";
            }

            return $"{code}:NO_MATERIAL_CHECK";
        }

        private static string? ResolveCoatingMaterialCodeForCanGroup(cost_estimate? est)
        {
            if (est == null)
                return null;

            if (!string.IsNullOrWhiteSpace(est.coating_material_code))
                return NormCanGroupCode(est.coating_material_code);

            var raw = NormCanGroupCode(est.coating_type);

            return raw switch
            {
                "KEO_NUOC" or "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" or "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" or "KEO_UV" or "PHU_UV" or "KEO_PHU_UV" => "KEO_PHU_UV",
                _ => string.IsNullOrWhiteSpace(raw) ? null : raw
            };
        }

        private async Task<cost_estimate?> LoadAcceptedEstimateByOrderIdForCanGroupAsync(
            int orderId,
            CancellationToken ct)
        {
            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            if (req == null)
                return null;

            cost_estimate? est = null;

            if (req.accepted_estimate_id.HasValue &&
                req.accepted_estimate_id.Value > 0)
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

            return est;
        }

        private static bool IsActualMissing(decimal? value)
        {
            return !value.HasValue;
        }

        private static bool IsPreviousStageInputForDetail(
            StageMaterialDto input,
            StageOutputRef? prevOutput)
        {
            if (input == null || prevOutput == null)
                return false;

            var inputCode = NormDetailCode(input.code);
            var inputName = NormDetailCode(input.name);

            var prevCode = NormDetailCode(prevOutput.Code);
            var prevName = NormDetailCode(prevOutput.Name);

            if (!string.IsNullOrWhiteSpace(prevCode) && inputCode == prevCode)
                return true;

            if (!string.IsNullOrWhiteSpace(prevName) && inputName == prevName)
                return true;

            return inputCode is "PREV" or "INPUT" or "BTP" or "REFERENCE"
                   || inputName.Contains("BAN_THANH_PHAM")
                   || inputName.Contains("BTP")
                   || inputName.Contains("GIAY_DA_CAT")
                   || inputName.Contains("CONG_DOAN_TRUOC");
        }

        private static void ApplyPreviousStageActualToInputMaterials(
    ProductionStageDto stage,
    StageOutputRef? prevOutput)
        {
            if (stage?.input_materials == null || stage.input_materials.Count == 0)
                return;

            if (prevOutput == null)
                return;

            /*
             * Chỉ lấy actual thật.
             * Không fallback sang estimated, vì user yêu cầu:
             * nếu DB/log thật sự không có thì thôi.
             */
            var prevActual = ToPositiveDecimalOrZero(prevOutput.ActualQuantity);

            if (prevActual <= 0)
                return;

            foreach (var input in stage.input_materials)
            {
                if (input == null)
                    continue;

                if (!IsActualMissing(input.actual_quantity))
                    continue;

                if (!IsPreviousStageInputForDetail(input, prevOutput))
                    continue;

                input.actual_quantity = Math.Round(prevActual, 4);
                input.quantity_source = "Actual";
            }
        }

        private static void ApplyPlateActualFromRaloToInStage(
    ProductionStageDto stage,
    List<ProductionStageDto> previousStages)
        {
            if (stage == null)
                return;

            var currentCode = NormDetailProcessCode(stage.process_code);

            if (!string.Equals(currentCode, "IN", StringComparison.OrdinalIgnoreCase))
                return;

            if (stage.input_materials == null || stage.input_materials.Count == 0)
                return;

            var raloActual = ResolveRaloActualPlateQuantity(previousStages);

            if (raloActual <= 0)
                return;

            foreach (var input in stage.input_materials)
            {
                if (input == null)
                    continue;

                if (!IsActualMissing(input.actual_quantity))
                    continue;

                var inputCode = NormDetailCode(input.code);
                var inputName = NormDetailCode(input.name);

                var isPlateInput =
                    IsPlateCode(inputCode) ||
                    IsPlateCode(inputName) ||
                    inputName.Contains("BAN_KEM") ||
                    inputName.Contains("KEM_IN");

                if (!isPlateInput)
                    continue;

                input.actual_quantity = Math.Round(raloActual, 4);
                input.quantity_source = "Actual";
            }
        }

        private static decimal ResolveRaloActualPlateQuantity(
            List<ProductionStageDto> previousStages)
        {
            if (previousStages == null || previousStages.Count == 0)
                return 0m;

            var raloStage = previousStages
                .LastOrDefault(x =>
                    string.Equals(
                        NormDetailProcessCode(x.process_code),
                        "RALO",
                        StringComparison.OrdinalIgnoreCase));

            if (raloStage == null)
                return 0m;

            /*
             * Ưu tiên output actual của RALO.
             */
            var fromOutput = ToPositiveDecimalOrZero(
                raloStage.output_product?.actual_quantity);

            if (fromOutput > 0)
                return fromOutput;

            /*
             * Fallback: input PLATE của RALO nếu có material_usage_json.
             */
            if (raloStage.input_materials != null)
            {
                var fromInput = raloStage.input_materials
                    .Where(x =>
                        x != null &&
                        (
                            IsPlateCode(x.code) ||
                            IsPlateCode(x.name)
                        ))
                    .Select(x => ToPositiveDecimalOrZero(x.actual_quantity))
                    .Where(x => x > 0)
                    .DefaultIfEmpty(0m)
                    .Max();

                if (fromInput > 0)
                    return fromInput;
            }

            if (raloStage.qty_good > 0)
                return raloStage.qty_good;

            return 0m;
        }

        private static decimal ToPositiveDecimalOrZero(decimal? value)
        {
            if (!value.HasValue)
                return 0m;

            return value.Value > 0m
                ? value.Value
                : 0m;
        }

        private static void SyncActualQuantityFieldsForProductionDetail(
    ProductionStageDto stage)
        {
            if (stage == null)
                return;

            if (stage.output_product != null)
            {
                var outputActual = ToPositiveDecimalOrZero(
                    stage.output_product.actual_quantity);

                if (outputActual > 0)
                {
                    stage.actual_output_quantity = Math.Round(outputActual, 4);

                    if (stage.qty_good <= 0)
                        stage.qty_good = (int)Math.Ceiling(outputActual);

                    stage.output_product.quantity_source = "Actual";
                }

                if (stage.estimated_output_quantity <= 0 &&
                    stage.output_product.estimated_quantity > 0)
                {
                    stage.estimated_output_quantity = stage.output_product.estimated_quantity;
                }
            }
        }

        private static string NormDetailProcessCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static bool IsSubProduction(production prod)
        {
            return string.Equals(
                prod.prod_method,
                "SUB",
                StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ParseDetailRoute(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormDetailProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<List<string>> ResolveSubProductProcessCodesForDetailAsync(
            production prod,
            CancellationToken ct)
        {
            /*
             * Ưu tiên lấy path của sub_product.
             * Ví dụ: RALO,CAT,IN.
             */
            if (prod.sub_product_id.HasValue && prod.sub_product_id.Value > 0)
            {
                var csv = await _db.sub_products
                    .AsNoTracking()
                    .Where(x => x.id == prod.sub_product_id.Value)
                    .Select(x => x.product_process)
                    .FirstOrDefaultAsync(ct);

                var codes = ParseDetailRoute(csv);

                if (codes.Count > 0)
                    return codes;
            }

            /*
             * Fallback:
             * Nếu DB không có sub_product.product_process,
             * suy ra từ các task đã lấy từ bán thành phẩm.
             */
            var taskCodes = await (
                from t in _db.tasks.AsNoTracking()

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where t.prod_id == prod.prod_id
                      && t.is_taken_sub_product == true

                orderby t.seq_num, t.task_id

                select pp != null ? pp.process_code : t.name
            )
            .ToListAsync(ct);

            return taskCodes
                .Select(NormDetailProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsFirstStageAfterSubProductForDetail(
            string? currentCode,
            IReadOnlyList<string> routeCodes,
            IReadOnlyList<string> subCodes)
        {
            var current = NormDetailProcessCode(currentCode);

            if (string.IsNullOrWhiteSpace(current))
                return false;

            if (routeCodes == null || routeCodes.Count == 0)
                return false;

            if (subCodes == null || subCodes.Count == 0)
                return false;

            var currentIndex = routeCodes
                .Select(NormDetailProcessCode)
                .ToList()
                .FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
                return false;

            var subIndexes = subCodes
                .Select(NormDetailProcessCode)
                .Select(code => routeCodes
                    .Select(NormDetailProcessCode)
                    .ToList()
                    .FindIndex(x => string.Equals(x, code, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .ToList();

            if (subIndexes.Count == 0)
                return false;

            var subLastIndex = subIndexes.Max();

            /*
             * Công đoạn đầu tiên sau sub_product.
             * Ví dụ:
             * route = RALO,CAT,IN,PHU,CAN
             * sub = RALO,CAT,IN
             * => PHU là currentIndex = subLastIndex + 1
             */
            return currentIndex == subLastIndex + 1;
        }

        private static string? ResolvePreviousProcessCodeForDetail(
            string? currentCode,
            IReadOnlyList<string> routeCodes)
        {
            var current = NormDetailProcessCode(currentCode);

            if (string.IsNullOrWhiteSpace(current))
                return null;

            var normalizedRoute = routeCodes
                .Select(NormDetailProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var idx = normalizedRoute.FindIndex(x =>
                string.Equals(x, current, StringComparison.OrdinalIgnoreCase));

            if (idx <= 0)
                return null;

            return normalizedRoute[idx - 1];
        }

        private async Task<decimal> ResolveActualFinishedQtyByProdAndProcessAsync(
            int prodId,
            string? processCode,
            CancellationToken ct)
        {
            var code = NormDetailProcessCode(processCode);

            if (prodId <= 0 || string.IsNullOrWhiteSpace(code))
                return 0m;

            var qty = await (
                from t in _db.tasks.AsNoTracking()

                join pp0 in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp0.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                join l0 in _db.task_logs.AsNoTracking()
                    on t.task_id equals l0.task_id into lj
                from l in lj.DefaultIfEmpty()

                where t.prod_id == prodId
                      && pp != null
                      && pp.process_code != null
                      && pp.process_code.Trim().ToUpper() == code
                      && l != null
                      && (
                            l.action_type == "Finished" ||
                            l.action_type == "FinishedByGroup"
                         )

                select (decimal?)(l.qty_good ?? 0)
            )
            .SumAsync(ct);

            return qty ?? 0m;
        }

        private static bool IsMainBtpInputForDetail(
            StageMaterialDto input,
            string? previousCode)
        {
            if (input == null)
                return false;

            var inputCode = NormDetailProcessCode(input.code);
            var inputName = NormDetailProcessCode(input.name);
            var prevCode = NormDetailProcessCode(previousCode);

            if (!string.IsNullOrWhiteSpace(prevCode) &&
                string.Equals(inputCode, prevCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return inputCode is "PREV" or "INPUT" or "BTP" or "REFERENCE"
                   || inputName.Contains("BAN_THANH_PHAM")
                   || inputName.Contains("BTP")
                   || inputName.Contains("CONG_DOAN")
                   || inputName.Contains("GIAY_DA_CAT");
        }

        private async Task ApplySubFirstDownstreamActualEstimateAsync(
    production prod,
    ProductionStageDto stage,
    IReadOnlyList<ProductTypeProcessStepDto> routeSteps,
    CancellationToken ct)
        {
            if (!IsSubProduction(prod))
                return;

            if (stage == null)
                return;

            if (stage.input_materials == null)
                stage.input_materials = new List<StageMaterialDto>();

            var currentCode = NormDetailProcessCode(stage.process_code);

            if (string.IsNullOrWhiteSpace(currentCode))
                return;

            var isGroupSub =
                string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(prod.prod_method, "SUB", StringComparison.OrdinalIgnoreCase);

            /*
             * CASE 1: GROUP + SUB
             *
             * Đây là case prod_id = 17.
             * Không dùng prod.sub_product_id vì group production không có sub_product_id.
             * Phải tính lại planned input từ task_links của group task.
             */
            if (isGroupSub)
            {
                var plan = await ResolveGroupSubStagePlanForDetailAsync(
                    groupProd: prod,
                    stage: stage,
                    currentProcessCode: currentCode,
                    ct: ct);

                if (plan == null || plan.PlannedInputQty <= 0)
                    return;

                var plannedInputQty = Math.Round(plan.PlannedInputQty, 4);
                var previousCode = plan.PreviousProcessCode;

                var mainInputs = stage.input_materials
                    .Where(x =>
                        IsMainBtpInputForDetail(x, previousCode) ||
                        IsLikelyBtpInputForDetail(x))
                    .ToList();

                /*
                 * Nếu BuildStageIO không tạo input BTP chính thì tự thêm.
                 */
                if (mainInputs.Count == 0)
                {
                    var newInput = new StageMaterialDto
                    {
                        name = $"Bán thành phẩm từ công đoạn {previousCode}",
                        code = previousCode,
                        quantity = plannedInputQty,
                        estimated_quantity = plannedInputQty,
                        actual_quantity = plannedInputQty,
                        quantity_source = "PlannedSubInputWithWaste",
                        unit = "tờ"
                    };

                    stage.input_materials.Insert(0, newInput);
                    mainInputs.Add(newInput);
                }

                foreach (var input in mainInputs)
                {
                    input.quantity = plannedInputQty;
                    input.estimated_quantity = plannedInputQty;
                    input.actual_quantity = plannedInputQty;
                    input.quantity_source = "PlannedSubInputWithWaste";

                    if (string.IsNullOrWhiteSpace(input.code))
                        input.code = previousCode;

                    if (string.IsNullOrWhiteSpace(input.name))
                        input.name = $"Bán thành phẩm từ công đoạn {previousCode}";

                    if (string.IsNullOrWhiteSpace(input.unit))
                        input.unit = "tờ";
                }

                /*
                 * Đồng bộ output estimate của stage với QR prepare.
                 * Vì QR prepare đang validate theo planned BTP input = 6290,
                 * detail cũng phải hiển thị PHU output estimate = 6290.
                 */
                if (stage.output_product != null)
                {
                    stage.output_product.quantity = plannedInputQty;
                    stage.output_product.estimated_quantity = plannedInputQty;

                    if (stage.output_product.actual_quantity <= 0)
                        stage.output_product.quantity_source = "PlannedSubInputWithWaste";
                }

                stage.estimated_output_quantity = plannedInputQty;

                return;
            }

            /*
             * CASE 2: SINGLE + SUB
             *
             * Với single SUB thì giữ logic lấy actual output của công đoạn trước.
             */
            var routeCodes = routeSteps == null
                ? new List<string>()
                : routeSteps
                    .Select(x => x.process_code)
                    .Select(NormDetailProcessCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (routeCodes.Count == 0)
                return;

            var subCodes = await ResolveSubProductProcessCodesForDetailAsync(
                prod,
                ct);

            if (subCodes.Count == 0)
                return;

            if (!IsCurrentAfterSubBoundaryForDetail(currentCode, routeCodes, subCodes))
                return;

            var singlePreviousCode = ResolvePreviousProcessCodeForDetail(
                currentCode,
                routeCodes);

            if (string.IsNullOrWhiteSpace(singlePreviousCode))
                return;

            var actualPrevQty = await ResolveActualFinishedQtyByProdAndProcessAsync(
                prod.prod_id,
                singlePreviousCode,
                ct);

            if (actualPrevQty <= 0)
                return;

            actualPrevQty = Math.Round(actualPrevQty, 4);

            foreach (var input in stage.input_materials)
            {
                if (!IsMainBtpInputForDetail(input, singlePreviousCode))
                    continue;

                input.quantity = actualPrevQty;
                input.estimated_quantity = actualPrevQty;
                input.actual_quantity = actualPrevQty;
                input.quantity_source = "ActualPreviousStage";
            }

            if (stage.output_product != null &&
                stage.output_product.estimated_quantity < actualPrevQty)
            {
                stage.output_product.quantity = actualPrevQty;
                stage.output_product.estimated_quantity = actualPrevQty;

                if (stage.output_product.actual_quantity <= 0)
                    stage.output_product.quantity_source = "EstimatedFromActualPreviousStage";
            }

            if (stage.estimated_output_quantity < actualPrevQty)
                stage.estimated_output_quantity = actualPrevQty;
        }

        private sealed class GroupSubStagePlanForDetail
        {
            public decimal PlannedInputQty { get; set; }

            public string PreviousProcessCode { get; set; } = "PREV";
        }

        private sealed class DetailOrderEstimateContext
        {
            public int order_id { get; set; }

            public int order_qty { get; set; }

            public int n_up { get; set; } = 1;

            public int sheets_required { get; set; }

            public string? coating_type { get; set; }

            public List<string> route_codes { get; set; } = new();
        }

        private async Task<GroupSubStagePlanForDetail?> ResolveGroupSubStagePlanForDetailAsync(
    production groupProd,
    ProductionStageDto stage,
    string currentProcessCode,
    CancellationToken ct)
        {
            if (groupProd == null)
                return null;

            if (stage == null || !stage.task_id.HasValue)
                return null;

            var groupTaskId = stage.task_id.Value;
            var currentCode = NormDetailProcessCode(currentProcessCode);

            if (string.IsNullOrWhiteSpace(currentCode))
                return null;

            var links = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.group_task_id == groupTaskId &&
                    (
                        x.status == null ||
                        x.status.ToUpper() != "CANCELLED"
                    ))
                .OrderBy(x => x.id)
                .ToListAsync(ct);

            if (links.Count == 0)
                return null;

            decimal totalPlannedInput = 0m;
            string? previousCodeForDisplay = null;

            foreach (var link in links)
            {
                var ctx = await LoadDetailOrderEstimateContextAsync(
                    link.order_id,
                    ct);

                if (ctx == null || ctx.route_codes.Count == 0)
                {
                    if (link.qty_plan > 0)
                        totalPlannedInput += link.qty_plan;

                    continue;
                }

                var currentIndex = ctx.route_codes.FindIndex(x =>
                    string.Equals(x, currentCode, StringComparison.OrdinalIgnoreCase));

                if (currentIndex <= 0)
                    continue;

                var previousCode = ctx.route_codes[currentIndex - 1];

                previousCodeForDisplay ??= previousCode;

                /*
                 * Với GROUP + SUB, sub path lấy từ single production đang link,
                 * không lấy từ group production vì group production.sub_product_id = null.
                 */
                var subCodes = await ResolveLinkedSingleSubProductProcessCodesForDetailAsync(
                    singleProdId: link.single_prod_id,
                    routeCodes: ctx.route_codes,
                    ct: ct);

                if (subCodes.Count == 0)
                    continue;

                var subLastIndex = subCodes
                    .Select(code => ctx.route_codes.FindIndex(x =>
                        string.Equals(x, code, StringComparison.OrdinalIgnoreCase)))
                    .Where(x => x >= 0)
                    .DefaultIfEmpty(-1)
                    .Max();

                /*
                 * Chỉ tính những công đoạn nằm sau sub boundary.
                 * Ví dụ sub = RALO,CAT,IN thì PHU/CAN hợp lệ.
                 */
                if (subLastIndex < 0 || currentIndex <= subLastIndex)
                    continue;

                var productQty = link.qty_plan > 0
                    ? link.qty_plan
                    : ctx.order_qty;

                if (productQty <= 0)
                    productQty = 1;

                var nUp = ctx.n_up > 0 ? ctx.n_up : 1;

                /*
                 * Không dùng thẳng estimate.sheets_required của cả order nếu link chỉ là một phần.
                 * Tính lại sheets base theo qty_plan của từng link.
                 */
                var sheetsBase = (int)Math.Ceiling(productQty / (decimal)nUp);

                if (sheetsBase <= 0)
                    sheetsBase = 1;

                var stageQty = SubProductionQuantityHelper.ResolveStageQty(
                    currentProcessCode: currentCode,
                    routeProcessCodes: ctx.route_codes,
                    productQty: productQty,
                    nUp: nUp,
                    explicitSheetsBase: sheetsBase,
                    coatingType: ctx.coating_type);

                /*
                 * Với detail và qr-prepare, số cần đồng bộ là planned BTP input.
                 * Ví dụ PHU group SUB: tổng 2 order = 6000, planned input = 6290.
                 */
                if (stageQty.input_qty > 0)
                    totalPlannedInput += stageQty.input_qty;
                else
                    totalPlannedInput += productQty;
            }

            totalPlannedInput = Math.Round(totalPlannedInput, 4);

            if (totalPlannedInput <= 0)
                return null;

            return new GroupSubStagePlanForDetail
            {
                PlannedInputQty = totalPlannedInput,
                PreviousProcessCode = string.IsNullOrWhiteSpace(previousCodeForDisplay)
                    ? "PREV"
                    : previousCodeForDisplay
            };
        }

        private async Task<List<string>> ResolveLinkedSingleSubProductProcessCodesForDetailAsync(
    int? singleProdId,
    IReadOnlyList<string> routeCodes,
    CancellationToken ct)
        {
            if (singleProdId.HasValue && singleProdId.Value > 0)
            {
                var singleProd = await _db.productions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.prod_id == singleProdId.Value, ct);

                if (singleProd != null)
                {
                    if (singleProd.sub_product_id.HasValue && singleProd.sub_product_id.Value > 0)
                    {
                        var subCsv = await _db.sub_products
                            .AsNoTracking()
                            .Where(x => x.id == singleProd.sub_product_id.Value)
                            .Select(x => x.product_process)
                            .FirstOrDefaultAsync(ct);

                        var fromSub = ParseDetailRoute(subCsv);

                        if (fromSub.Count > 0)
                            return fromSub;
                    }

                    /*
                     * Fallback: lấy các task đã được auto Finished do lấy SUB.
                     */
                    var fromTakenTasks = await (
                        from t in _db.tasks.AsNoTracking()

                        join pp0 in _db.product_type_processes.AsNoTracking()
                            on t.process_id equals pp0.process_id into ppj
                        from pp in ppj.DefaultIfEmpty()

                        where t.prod_id == singleProd.prod_id
                              && t.is_taken_sub_product == true

                        orderby t.seq_num, t.task_id

                        select pp != null ? pp.process_code : t.name
                    )
                    .ToListAsync(ct);

                    var takenCodes = fromTakenTasks
                        .Select(NormDetailProcessCode)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (takenCodes.Count > 0)
                        return takenCodes;
                }
            }

            /*
             * Fallback cuối:
             * Với nghiệp vụ hiện tại, SUB đang cover RALO,CAT,IN.
             * Chỉ trả các code này nếu route thật có chúng.
             */
            var defaultSub = new[] { "RALO", "CAT", "IN" }
                .Where(x => routeCodes.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();

            return defaultSub;
        }

        private static bool IsLikelyBtpInputForDetail(StageMaterialDto input)
        {
            if (input == null)
                return false;

            var inputCode = NormDetailProcessCode(input.code);
            var inputName = NormDetailProcessCode(input.name);

            if (inputCode is "RALO" or "CAT" or "IN" or "PHU" or "CAN" or "CAN_MANG" or "BOI" or "BE" or "DUT" or "DAN")
                return true;

            return inputName.Contains("BAN_THANH_PHAM")
                   || inputName.Contains("BTP")
                   || inputName.Contains("CONG_DOAN")
                   || inputName.Contains("GIAY_DA_CAT");
        }

        private static bool IsCurrentAfterSubBoundaryForDetail(
    string? currentProcessCode,
    IReadOnlyList<string> routeCodes,
    IReadOnlyList<string> subCodes)
        {
            var route = routeCodes
                .Select(NormDetailProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var current = NormDetailProcessCode(currentProcessCode);

            if (route.Count == 0 || string.IsNullOrWhiteSpace(current))
                return false;

            var currentIndex = route.FindIndex(x =>
                string.Equals(x, current, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
                return false;

            var subLastIndex = subCodes
                .Select(NormDetailProcessCode)
                .Select(code => route.FindIndex(x =>
                    string.Equals(x, code, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .DefaultIfEmpty(-1)
                .Max();

            return subLastIndex >= 0 && currentIndex > subLastIndex;
        }
        private async Task<DetailOrderEstimateContext?> LoadDetailOrderEstimateContextAsync(
    int orderId,
    CancellationToken ct)
        {
            if (orderId <= 0)
                return null;

            var item = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => new
                {
                    x.order_id,
                    x.quantity,
                    x.production_process
                })
                .FirstOrDefaultAsync(ct);

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderByDescending(x => x.order_request_id)
                .FirstOrDefaultAsync(ct);

            cost_estimate? est = null;

            if (req != null)
            {
                if (req.accepted_estimate_id.HasValue &&
                    req.accepted_estimate_id.Value > 0)
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
            }

            var orderQty = item?.quantity ?? 0;

            if (orderQty <= 0 && req?.quantity.HasValue == true)
                orderQty = req.quantity.Value;

            if (orderQty <= 0)
                orderQty = 1;

            var routeCsv = !string.IsNullOrWhiteSpace(item?.production_process)
                ? item!.production_process
                : est?.production_processes;

            var routeCodes = ParseDetailRoute(routeCsv);

            var nUp = 1;

            if (est != null && est.n_up > 0)
                nUp = est.n_up;

            var sheetsRequired = 0;

            if (est != null && est.sheets_required > 0)
                sheetsRequired = est.sheets_required;

            return new DetailOrderEstimateContext
            {
                order_id = orderId,
                order_qty = orderQty,
                n_up = nUp,
                sheets_required = sheetsRequired,
                coating_type = est?.coating_type,
                route_codes = routeCodes
            };
        }

        private static void ApplyOutputActualFallbackFromQtyGood(
    ProductionStageDto stage)
        {
            if (stage?.output_product == null)
                return;

            if (stage.output_product.actual_quantity > 0)
                return;

            if (stage.qty_good <= 0)
                return;

            stage.output_product.actual_quantity = stage.qty_good;
            stage.output_product.quantity_source = "Actual";
            stage.actual_output_quantity = stage.qty_good;
        }

        private static void NormalizeNullActualInputMaterials(
    ProductionStageDto stage)
        {
            if (stage?.input_materials == null || stage.input_materials.Count == 0)
                return;

            foreach (var input in stage.input_materials)
            {
                if (input.actual_quantity < 0)
                    input.actual_quantity = 0m;

                if (input.actual_quantity <= 0 &&
                    string.IsNullOrWhiteSpace(input.quantity_source))
                {
                    input.quantity_source = "NotReported";
                }
            }
        }

        public async Task<List<production>> GetProductionsByTaskIdAsync(
    int taskId,
    CancellationToken ct = default)
        {
            if (taskId <= 0)
                return new List<production>();

            // 1. Production trực tiếp của task
            var directProdIds = await _db.tasks
                .AsNoTracking()
                .Where(x =>
                    x.task_id == taskId &&
                    x.prod_id != null)
                .Select(x => x.prod_id!.Value)
                .ToListAsync(ct);

            // 2. Production liên quan qua task_links
            // Trường hợp task_id là single_task_id hoặc group_task_id
            var linkRows = await _db.task_links
                .AsNoTracking()
                .Where(x =>
                    x.single_task_id == taskId ||
                    x.group_task_id == taskId)
                .Select(x => new
                {
                    SingleProdId = (int?)x.single_prod_id,
                    GroupProdId = (int?)x.group_prod_id
                })
                .ToListAsync(ct);

            var linkedProdIds = new List<int>();

            foreach (var link in linkRows)
            {
                if (link.SingleProdId.HasValue && link.SingleProdId.Value > 0)
                    linkedProdIds.Add(link.SingleProdId.Value);

                if (link.GroupProdId.HasValue && link.GroupProdId.Value > 0)
                    linkedProdIds.Add(link.GroupProdId.Value);
            }

            var prodIds = directProdIds
                .Concat(linkedProdIds)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (prodIds.Count == 0)
                return new List<production>();

            var productions = await _db.productions
                .AsNoTracking()
                .Where(x => prodIds.Contains(x.prod_id))
                .OrderByDescending(x => x.prod_id)
                .ToListAsync(ct);

            return productions;
        }
    }
}