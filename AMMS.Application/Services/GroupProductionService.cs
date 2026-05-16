using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Productions.Groups;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AMMS.Application.Services
{
    public class GroupProductionService : IGroupProductionService
    {
        private readonly AppDbContext _db;
        private static readonly string[] Dept1Codes = { "RALO", "CAT", "IN" };
        private static readonly string[] Dept2Codes = { "PHU", "CAN", "CAN_MANG", "BOI" };
        private static readonly string[] Dept3Codes = { "BE", "DUT", "DAN" };
        private static readonly string[] FullRouteOrder =
        { "RALO", "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public GroupProductionService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<List<GroupProductionCandidateDto>> GetCandidatesAsync(
    int? productTypeId,
    string? processCodes,
    CancellationToken ct = default)
        {
            var selectedCodes = GroupProductionHelper.ParseCodes(processCodes);

            if (selectedCodes.Count > 0)
                GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            var today = AppTime.NowVnUnspecified().Date;
            var minDeliveryDate = today.AddDays(7);

            var rows = await (
                from o in _db.orders.AsNoTracking()
                join pr in _db.productions.AsNoTracking()
                    on o.order_id equals pr.order_id
                where pr.prod_kind == "SINGLE"
                      && o.layout_confirmed
                      && o.is_production_ready

                      // Rule mới: chỉ order Scheduled mới được ghép.
                      && o.status == "Scheduled"

                      // Giữ rule 7 ngày nếu bạn vẫn cần.
                      && o.delivery_date != null
                      && o.delivery_date >= minDeliveryDate

                      // Không hiện order đã nằm trong production GROUP active.
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
                    o.order_id,
                    order_code = o.code,
                    single_prod_id = pr.prod_id,
                    o.delivery_date,
                    item = _db.order_items.AsNoTracking()
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .Select(i => new
                        {
                            i.product_type_id,
                            i.product_name,
                            i.quantity,
                            i.production_process
                        })
                        .FirstOrDefault()
                }
            ).ToListAsync(ct);

            var result = new List<GroupProductionCandidateDto>();

            foreach (var row in rows)
            {
                if (row.item == null)
                    continue;

                if (productTypeId.HasValue && row.item.product_type_id != productTypeId.Value)
                    continue;

                var orderCodes = GroupProductionHelper.ParseCodes(row.item.production_process);
                var processKey = string.Join(",", orderCodes);

                var hasAllSelectedCodes = selectedCodes.Count == 0 ||
                                          selectedCodes.All(x =>
                                              orderCodes.Contains(x, StringComparer.OrdinalIgnoreCase));

                var productTypeName = row.item.product_type_id.HasValue
                    ? await _db.product_types.AsNoTracking()
                        .Where(x => x.product_type_id == row.item.product_type_id.Value)
                        .Select(x => x.name)
                        .FirstOrDefaultAsync(ct)
                    : null;

                result.Add(new GroupProductionCandidateDto
                {
                    order_id = row.order_id,
                    order_code = row.order_code,
                    single_prod_id = row.single_prod_id,
                    product_type_id = row.item.product_type_id,
                    product_type_name = productTypeName,
                    product_name = row.item.product_name,
                    quantity = row.item.quantity,
                    production_process = row.item.production_process,
                    process_key = processKey,
                    delivery_date = row.delivery_date,
                    can_group = hasAllSelectedCodes,
                    reason = hasAllSelectedCodes
                        ? null
                        : "Order không có đủ các công đoạn được chọn để ghép."
                });
            }

            return result
                .OrderBy(x => x.product_type_id)
                .ThenBy(x => x.delivery_date)
                .ThenBy(x => x.order_id)
                .ToList();
        }

        public async Task<CreateGroupProductionResponse> CreateAsync(
    CreateGroupProductionRequest req,
    int? managerUserId,
    CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            var orderIds = req.order_ids
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (orderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để sản xuất ghép.");

            var selectedCodes = req.process_codes
                .Select(GroupProductionHelper.Norm)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn để tạo lệnh sản xuất ghép/tách.");

            GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            var strategy = _db.Database.CreateExecutionStrategy();

            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var rows = await LoadGroupOrderRowsAsync(orderIds, ct);

                if (rows.Count != orderIds.Count)
                    throw new InvalidOperationException("Một số order không tồn tại hoặc chưa có production riêng.");

                if (rows.Any(x => x.Item == null))
                    throw new InvalidOperationException("Một số order chưa có order_item.");

                var invalidStatusOrders = rows
                    .Where(x => !string.Equals(x.Order.status, "Scheduled", StringComparison.OrdinalIgnoreCase))
                    .Select(x => $"{x.Order.order_id}({x.Order.status})")
                    .ToList();

                if (invalidStatusOrders.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Chỉ order có status Scheduled mới được ghép. Order không hợp lệ: {string.Join(", ", invalidStatusOrders)}");
                }

                if (rows.Any(x => !x.Order.layout_confirmed || !x.Order.is_production_ready))
                    throw new InvalidOperationException("Tất cả order phải xác nhận layout và sẵn sàng sản xuất.");

                var productTypeIds = rows
                    .Select(x => x.Item.product_type_id)
                    .Distinct()
                    .ToList();

                if (productTypeIds.Count != 1 || productTypeIds[0] == null)
                    throw new InvalidOperationException("Các order phải cùng product_type.");

                var productTypeId = productTypeIds[0]!.Value;

                var alreadyGroupedOrderIds = await _db.prod_orders
                    .Where(x => orderIds.Contains(x.order_id) && x.status == "Active")
                    .Where(x => _db.productions.Any(p =>
                        p.prod_id == x.prod_id &&
                        p.prod_kind == "GROUP" &&
                        p.status != "Cancelled" &&
                        p.status != "Completed"))
                    .Select(x => x.order_id)
                    .Distinct()
                    .ToListAsync(ct);

                if (alreadyGroupedOrderIds.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Một số order đã nằm trong production ghép active: {string.Join(",", alreadyGroupedOrderIds)}");
                }

                var plan = BuildDepartmentProductionPlan(
                    rows,
                    selectedCodes,
                    out var warnings);

                if (plan.Count == 0)
                    throw new InvalidOperationException("Không có công đoạn hợp lệ để tạo lệnh sản xuất.");

                var now = AppTime.NowVnUnspecified();

                var allSteps = await _db.product_type_processes
                    .Where(x => x.product_type_id == productTypeId && (x.is_active ?? true))
                    .OrderBy(x => x.seq_num)
                    .ToListAsync(ct);

                var createdGroupProdIds = new List<int>();
                var createdSplitProdIds = new List<int>();

                foreach (var segment in plan)
                {
                    if (segment.IsGroup)
                    {
                        var groupProd = await CreateDepartmentGroupProductionAsync(
                            segment,
                            productTypeId,
                            managerUserId,
                            req.planned_start_date ?? now,
                            req.note,
                            allSteps,
                            ct);

                        createdGroupProdIds.Add(groupProd.prod_id);
                    }
                    else
                    {
                        var splitProd = await CreateSplitProductionAsync(
                            segment,
                            productTypeId,
                            managerUserId,
                            req.planned_start_date ?? now,
                            req.note,
                            allSteps,
                            ct);

                        createdSplitProdIds.Add(splitProd.prod_id);
                    }
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var firstGroupId = createdGroupProdIds.FirstOrDefault();

                var firstGroupCode = firstGroupId > 0
                    ? await _db.productions.AsNoTracking()
                        .Where(x => x.prod_id == firstGroupId)
                        .Select(x => x.code)
                        .FirstOrDefaultAsync(ct)
                    : null;

                return new CreateGroupProductionResponse
                {
                    group_prod_id = firstGroupId,
                    code = firstGroupCode,

                    group_prod_ids = createdGroupProdIds,
                    split_prod_ids = createdSplitProdIds,
                    all_created_prod_ids = createdGroupProdIds
                        .Concat(createdSplitProdIds)
                        .Distinct()
                        .ToList(),
                    order_ids = orderIds,
                    warnings = warnings,
                    message = "Đã tạo production theo phòng ban, path công đoạn và điều kiện NVL."
                };
            });
        }

        private async Task<production> CreateDepartmentGroupProductionAsync(
    ProductionPlanSegment segment,
    int productTypeId,
    int? managerUserId,
    DateTime plannedStart,
    string? note,
    List<product_type_process> allSteps,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();
            var codesCsv = string.Join(",", segment.ProcessCodes);

            var groupCode = await GenerateShortProductionCodeAsync(
                "GRP",
                segment.DepartmentCode,
                ct);

            var groupProd = new production
            {
                code = groupCode,
                order_id = null,
                manager_id = managerUserId,
                created_at = now,
                planned_start_date = plannedStart,
                status = "Scheduled",
                product_type_id = productTypeId,
                note = string.IsNullOrWhiteSpace(note)
                    ? $"Group {segment.DepartmentName}: {codesCsv}"
                    : $"{note} | Group {segment.DepartmentName}: {codesCsv}",
                prod_kind = "GROUP",
                prod_method = "GROUP",
                group_process_codes = codesCsv,
                group_total_qty = segment.Members.Sum(x => x.Item.quantity)
            };

            await _db.productions.AddAsync(groupProd, ct);
            await _db.SaveChangesAsync(ct);

            foreach (var member in segment.Members)
            {
                await _db.prod_orders.AddAsync(new prod_order
                {
                    prod_id = groupProd.prod_id,
                    order_id = member.Order.order_id,
                    single_prod_id = member.SingleProd.prod_id,
                    qty = member.Item.quantity,
                    product_type_id = productTypeId,
                    product_process = member.Item.production_process,
                    status = "Active",
                    created_at = now
                }, ct);
            }

            var stepRows = allSteps
                .Where(x => segment.ProcessCodes.Contains(
                    GroupProductionHelper.Norm(x.process_code),
                    StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x.seq_num)
                .ToList();

            if (stepRows.Count != segment.ProcessCodes.Count)
            {
                var foundCodes = stepRows
                    .Select(x => GroupProductionHelper.Norm(x.process_code))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missing = segment.ProcessCodes
                    .Where(x => !foundCodes.Contains(x))
                    .ToList();

                throw new InvalidOperationException(
                    $"Không tìm thấy đủ process trong product_type_processes. Thiếu: {string.Join(",", missing)}");
            }

            var groupTasks = new List<task>();
            var seq = 1;

            foreach (var step in stepRows)
            {
                var groupTask = new task
                {
                    prod_id = groupProd.prod_id,
                    name = $"GROUP-{segment.DepartmentCode}-{step.process_name ?? step.process_code}",
                    seq_num = seq++,
                    status = "Unassigned",
                    machine = step.machine,
                    process_id = step.process_id,
                    input_mode = "MANUAL",
                    reason = $"Task thuộc production ghép phòng ban {segment.DepartmentName}, nhập tay input/output khi báo cáo."
                };

                await _db.tasks.AddAsync(groupTask, ct);
                groupTasks.Add(groupTask);
            }

            await _db.SaveChangesAsync(ct);

            await LinkSingleTasksAsync(
                groupProd,
                groupTasks,
                segment.Members.Select(x => new SingleRow
                {
                    OrderId = x.Order.order_id,
                    SingleProdId = x.SingleProd.prod_id,
                    Qty = x.Item.quantity
                }).ToList(),
                ct);

            await _db.SaveChangesAsync(ct);

            return groupProd;
        }

        private async Task<production> CreateSplitProductionAsync(
    ProductionPlanSegment segment,
    int productTypeId,
    int? managerUserId,
    DateTime plannedStart,
    string? note,
    List<product_type_process> allSteps,
    CancellationToken ct)
        {
            var member = segment.Members.First();
            var now = AppTime.NowVnUnspecified();
            var codesCsv = string.Join(",", segment.ProcessCodes);

            var splitCode = await GenerateShortProductionCodeAsync(
                "SPL",
                segment.DepartmentCode,
                ct);

            var splitProd = new production
            {
                code = splitCode,
                order_id = member.Order.order_id,
                manager_id = managerUserId,
                created_at = now,
                planned_start_date = plannedStart,
                status = "Scheduled",
                product_type_id = productTypeId,
                note = string.IsNullOrWhiteSpace(note)
                    ? $"Split {segment.DepartmentName}: {codesCsv}"
                    : $"{note} | Split {segment.DepartmentName}: {codesCsv}",
                prod_kind = "SPLIT",
                prod_method = "SPLIT",
                group_process_codes = codesCsv,
                group_total_qty = member.Item.quantity
            };

            await _db.productions.AddAsync(splitProd, ct);
            await _db.SaveChangesAsync(ct);

            /*
             * Move task từ SINGLE sang SPLIT để tránh trùng lệnh sản xuất.
             * Ví dụ order 2 có DAN riêng:
             * - DAN được move khỏi SINGLE order 2
             * - DAN nằm trong production SPLIT order 2
             */
            var existingTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == member.SingleProd.prod_id)
                .ToListAsync(ct);

            var processSeqMap = segment.ProcessCodes
    .Select((code, index) => new
    {
        code = NormProcessCode(code),
        seq = index + 1
    })
    .ToDictionary(x => x.code, x => x.seq, StringComparer.OrdinalIgnoreCase);

            var movedCount = 0;

            foreach (var task in existingTasks)
            {
                var taskCode = NormProcessCode(task.process?.process_code);

                if (!processSeqMap.ContainsKey(taskCode))
                    continue;

                task.prod_id = splitProd.prod_id;
                task.seq_num = processSeqMap[taskCode];
                task.status = "Unassigned";
                task.start_time = null;
                task.end_time = null;
                task.reason = $"Task được tách sang production {splitProd.code} theo phòng ban {segment.DepartmentName}.";

                movedCount++;
            }

            if (movedCount == 0)
            {
                var stepRows = allSteps
                    .Where(x => segment.ProcessCodes.Contains(
                        NormProcessCode(x.process_code),
                        StringComparer.OrdinalIgnoreCase))
                    .OrderBy(x => FullRouteIndex(x.process_code))
                    .ToList();

                foreach (var step in stepRows)
                {
                    var code = NormProcessCode(step.process_code);

                    await _db.tasks.AddAsync(new task
                    {
                        prod_id = splitProd.prod_id,
                        name = $"SPLIT-{step.process_name ?? step.process_code}",
                        seq_num = processSeqMap.TryGetValue(code, out var seq) ? seq : 999,
                        status = "Unassigned",
                        machine = step.machine,
                        process_id = step.process_id,
                        input_mode = "MANUAL",
                        reason = $"Task SPLIT theo phòng ban {segment.DepartmentName}."
                    }, ct);
                }
            }

            await _db.SaveChangesAsync(ct);

            return splitProd;
        }

        public async Task<List<SuggestedGroupProductionDto>> SuggestAsync(
    int? productTypeId,
    CancellationToken ct = default)
        {
            var candidates = await GetCandidatesAsync(productTypeId, null, ct);

            var orderIds = candidates
                .Select(x => x.order_id)
                .Distinct()
                .ToList();

            if (orderIds.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            var rows = await LoadGroupOrderRowsAsync(orderIds, ct);

            if (productTypeId.HasValue)
            {
                rows = rows
                    .Where(x => x.Item.product_type_id == productTypeId.Value)
                    .ToList();
            }

            var suggestions = new List<SuggestedGroupProductionDto>();

            var allPossibleCodes = rows
                .SelectMany(x => x.RouteCodes)
                .Where(x => !IsDept1(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => DepartmentOrder(ResolveDepartmentCode(x)))
                .ThenBy(x => GetGlobalRouteIndex(rows, x))
                .ToList();

            foreach (var processCode in allPossibleCodes)
            {
                var rowsWithProcess = rows
                    .Where(x => x.RouteCodes.Contains(processCode, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (rowsWithProcess.Count < 2)
                    continue;

                if (RequiresSameMaterialKey(processCode))
                {
                    var materialGroups = rowsWithProcess
                        .GroupBy(x => ResolveMaterialGroupKey(processCode, x))
                        .Where(g => g.Count() >= 2)
                        .ToList();

                    foreach (var mg in materialGroups)
                    {
                        suggestions.Add(new SuggestedGroupProductionDto
                        {
                            suggest_order = mg.Select(x => x.Order.order_id).Distinct().OrderBy(x => x).ToList(),
                            suggest_process = new List<string> { processCode },
                            department_code = ResolveDepartmentCode(processCode),
                            department_name = ResolveDepartmentName(ResolveDepartmentCode(processCode)),
                            material_key = mg.Key,
                            reason = $"Các order cùng công đoạn {processCode} và cùng mã NVL/cấu hình vật tư."
                        });
                    }
                }
                else
                {
                    suggestions.Add(new SuggestedGroupProductionDto
                    {
                        suggest_order = rowsWithProcess
                            .Select(x => x.Order.order_id)
                            .Distinct()
                            .OrderBy(x => x)
                            .ToList(),

                        suggest_process = new List<string> { processCode },

                        department_code = ResolveDepartmentCode(processCode),
                        department_name = ResolveDepartmentName(ResolveDepartmentCode(processCode)),

                        material_key = null,

                        reason = $"Các order cùng công đoạn {processCode}."
                    });
                }
            }

            return MergeSuggestions(suggestions);
        }

        public async Task<GroupProductionTaskContextDto?> GetTaskContextAsync(
    int taskId,
    CancellationToken ct = default)
        {
            var current = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Include(x => x.prod)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (current == null)
                return null;

            task? previous = null;

            if (current.prod_id.HasValue)
            {
                var currentSeq = current.seq_num ?? int.MaxValue;

                previous = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.process)
                    .Where(x =>
                        x.prod_id == current.prod_id.Value &&
                        x.task_id != current.task_id &&
                        (x.seq_num ?? int.MaxValue) < currentSeq)
                    .OrderByDescending(x => x.seq_num)
                    .ThenByDescending(x => x.task_id)
                    .FirstOrDefaultAsync(ct);
            }

            return new GroupProductionTaskContextDto
            {
                task_id = current.task_id,
                prod_id = current.prod_id,
                prod_kind = current.prod?.prod_kind,
                process_code = current.process?.process_code,
                process_name = current.process?.process_name,
                status = current.status,

                previous_task = previous == null
                    ? null
                    : new TaskPreviousInfoDto
                    {
                        task_id = previous.task_id,
                        prod_id = previous.prod_id,
                        seq_num = previous.seq_num,
                        process_code = previous.process?.process_code,
                        process_name = previous.process?.process_name,
                        status = previous.status,
                        start_time = previous.start_time,
                        end_time = previous.end_time
                    }
            };
        }

        private static List<SuggestedGroupProductionDto> MergeSuggestions(
            List<SuggestedGroupProductionDto> suggestions)
        {
            var result = new List<SuggestedGroupProductionDto>();

            foreach (var item in suggestions)
            {
                var last = result.LastOrDefault();

                if (last != null &&
                    last.department_code == item.department_code &&
                    string.Equals(last.material_key, item.material_key, StringComparison.OrdinalIgnoreCase) &&
                    last.suggest_order.SequenceEqual(item.suggest_order))
                {
                    last.suggest_process.AddRange(item.suggest_process);
                    continue;
                }

                result.Add(item);
            }

            return result
                .Where(x => x.suggest_order.Count >= 2 && x.suggest_process.Count > 0)
                .ToList();
        }

        public async Task StartAsync(int groupProdId, CancellationToken ct = default)
        {
            var prod = await _db.productions
                .FirstOrDefaultAsync(x => x.prod_id == groupProdId, ct);

            if (prod == null)
                throw new KeyNotFoundException("Production not found.");

            if (!string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prod.prod_kind, "SPLIT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("API này chỉ dùng cho production GROUP/SPLIT.");
            }

            if (!string.Equals(prod.status, "Scheduled", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(prod.status, "Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Chỉ production Scheduled mới được bắt đầu. Trạng thái hiện tại: {prod.status}");
            }

            var dep = await ProductionDependencyValidator.CheckProductionCanStartAsync(
                _db,
                groupProdId,
                ct);

            if (!dep.can_start)
            {
                throw new InvalidOperationException(
                    "Chưa thể bắt đầu production vì công đoạn trước đó chưa hoàn thành. " +
                    dep.message);
            }

            var now = AppTime.NowVnUnspecified();

            prod.status = "InProcessing";
            prod.actual_start_date ??= now;

            await _db.SaveChangesAsync(ct);
        }

        public async Task<GroupProductionDetailDto?> GetDetailAsync(
            int groupProdId,
            CancellationToken ct = default)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == groupProdId, ct);

            if (prod == null)
                return null;

            if (!string.Equals(prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production này không phải production ghép.");

            var productTypeName = prod.product_type_id.HasValue
                ? await _db.product_types.AsNoTracking()
                    .Where(x => x.product_type_id == prod.product_type_id.Value)
                    .Select(x => x.name)
                    .FirstOrDefaultAsync(ct)
                : null;

            var orderRows = await (
                from po in _db.prod_orders.AsNoTracking()
                join o in _db.orders.AsNoTracking() on po.order_id equals o.order_id
                where po.prod_id == groupProdId && po.status == "Active"
                orderby po.id
                select new GroupProductionOrderDto
                {
                    order_id = po.order_id,
                    order_code = o.code,
                    single_prod_id = po.single_prod_id ?? 0,
                    qty = po.qty,
                    status = o.status
                }
            ).ToListAsync(ct);

            var tasks = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == groupProdId)
                .OrderBy(x => x.seq_num)
                .ToListAsync(ct);

            var taskIds = tasks.Select(x => x.task_id).ToList();

            var logs = await _db.task_logs
                .AsNoTracking()
                .Where(x => x.task_id.HasValue && taskIds.Contains(x.task_id.Value))
                .OrderBy(x => x.log_time)
                .Select(x => new GroupTaskLogDto
                {
                    log_id = x.log_id,
                    task_id = x.task_id!.Value,
                    action_type = x.action_type,
                    qty_good = x.qty_good ?? 0,
                    log_time = x.log_time,
                    reason = x.reason,

                    report_image_url = x.report_image_url,

                    reference_input_json = x.reference_input_json,
                    material_usage_json = x.material_usage_json,
                    output_json = x.output_json
                })
                .ToListAsync(ct);

            foreach (var log in logs)
            {
                log.report_image_urls = SplitImageUrls(log.report_image_url);
            }

            var allocations = await (
                from tq in _db.task_qtys.AsNoTracking()
                join o in _db.orders.AsNoTracking() on tq.order_id equals o.order_id
                where taskIds.Contains(tq.group_task_id)
                select new
                {
                    tq.group_task_id,
                    tq.order_id,
                    order_code = o.code,
                    tq.single_task_id,
                    tq.qty_good,
                    tq.output_json
                }
            ).ToListAsync(ct);

            var stages = new List<GroupProductionStageDto>();
            var previousOutputQty = (decimal)prod.group_total_qty;
            var previousOutputName = "Bán thành phẩm từ các order ghép";

            foreach (var task in tasks)
            {
                var taskLogs = logs
                    .Where(x => x.task_id == task.task_id)
                    .ToList();

                var stageAllocations = allocations
                    .Where(x => x.group_task_id == task.task_id)
                    .Select(x => new GroupTaskAllocationDto
                    {
                        order_id = x.order_id,
                        order_code = x.order_code,
                        single_task_id = x.single_task_id,
                        qty_good = x.qty_good,
                        output_json = x.output_json
                    })
                    .ToList();

                var io = BuildGroupStageIO(
                    task.process?.process_code,
                    task.process?.process_name,
                    prod.group_total_qty,
                    previousOutputQty,
                    previousOutputName,
                    taskLogs);

                var actualOutput = taskLogs.Sum(x => x.qty_good);

                stages.Add(new GroupProductionStageDto
                {
                    task_id = task.task_id,
                    seq_num = task.seq_num,
                    process_code = task.process?.process_code,
                    process_name = task.process?.process_name,
                    status = task.status,
                    start_time = task.start_time,
                    end_time = task.end_time,

                    estimated_output_qty = io.estimatedOutputQty,
                    actual_output_qty = actualOutput,

                    report_image_urls = taskLogs
                        .SelectMany(x => x.report_image_urls)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),

                    input_materials = io.inputs,
                    outputs = io.outputs,
                    logs = taskLogs,
                    allocations = stageAllocations
                });

                previousOutputQty = actualOutput > 0 ? actualOutput : io.estimatedOutputQty;
                previousOutputName = io.outputs.FirstOrDefault()?.name ?? $"BTP sau {task.process?.process_code}";
            }

            var previousStageContext = await BuildPreviousStageContextForGroupAsync(
                prod,
                tasks,
                orderRows,
                ct);

            var dep = await ProductionDependencyValidator.CheckProductionCanStartAsync(
                _db,
                prod.prod_id,
                ct);

            var isScheduled =
                string.Equals(prod.status, "Scheduled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prod.status, "Unassigned", StringComparison.OrdinalIgnoreCase);

            var canStart = isScheduled && dep.can_start;
            return new GroupProductionDetailDto
            {
                prod_id = prod.prod_id,
                code = prod.code,
                status = prod.status,

                can_start = canStart,
                can_start_message = canStart ? "Có thể bắt đầu production." : dep.message,

                product_type_id = prod.product_type_id,
                product_type_name = productTypeName,
                total_qty = prod.group_total_qty,
                process_codes = prod.group_process_codes,
                orders = orderRows,
                stages = stages,
                previous_stage_context = previousStageContext
            };
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

        private async Task<GroupProductionPreviousStageContextDto?> BuildPreviousStageContextForGroupAsync(
    production groupProd,
    List<task> groupTasks,
    List<GroupProductionOrderDto> orderRows,
    CancellationToken ct)
        {
            var firstGroupTask = groupTasks
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.task_id)
                .FirstOrDefault();

            if (firstGroupTask == null)
                return null;

            var currentCode = GroupProductionHelper.Norm(firstGroupTask.process?.process_code);

            if (string.IsNullOrWhiteSpace(currentCode))
                return null;

            var result = new GroupProductionPreviousStageContextDto
            {
                current_group_task_id = firstGroupTask.task_id,
                current_group_prod_id = groupProd.prod_id,
                current_process_code = firstGroupTask.process?.process_code,
                current_process_name = firstGroupTask.process?.process_name,
                previous_process_code = null,
                all_previous_finished = true
            };

            foreach (var orderRow in orderRows)
            {
                var route = await GetOrderRouteCodesAsync(orderRow.order_id, ct);

                var previousCode = ResolvePreviousProcessCode(route, currentCode);

                if (!string.IsNullOrWhiteSpace(previousCode))
                    result.previous_process_code ??= previousCode;

                if (string.IsNullOrWhiteSpace(previousCode))
                {
                    result.previous_tasks.Add(new GroupProductionPreviousTaskByOrderDto
                    {
                        order_id = orderRow.order_id,
                        order_code = orderRow.order_code,
                        previous_process_code = null,
                        is_finished = true,
                        message = $"Order {orderRow.order_id}: công đoạn {currentCode} là công đoạn đầu tiên trong path, không có công đoạn trước."
                    });

                    continue;
                }

                var previousTask = await FindPreviousProcessTaskForOrderAsync(
                    orderRow.order_id,
                    previousCode,
                    ct);

                if (previousTask == null)
                {
                    result.all_previous_finished = false;

                    result.previous_tasks.Add(new GroupProductionPreviousTaskByOrderDto
                    {
                        order_id = orderRow.order_id,
                        order_code = orderRow.order_code,
                        previous_process_code = previousCode,
                        previous_task_status = null,
                        is_finished = false,
                        message = $"Order {orderRow.order_id}: không tìm thấy task công đoạn trước {previousCode}."
                    });

                    continue;
                }

                var isFinished = IsTaskFinished(
                    previousTask.status,
                    previousTask.end_time);

                if (!isFinished)
                    result.all_previous_finished = false;

                result.previous_tasks.Add(new GroupProductionPreviousTaskByOrderDto
                {
                    order_id = orderRow.order_id,
                    order_code = orderRow.order_code,

                    previous_task_id = previousTask.task_id,
                    previous_prod_id = previousTask.prod_id,
                    previous_prod_kind = previousTask.prod_kind,
                    previous_seq_num = previousTask.seq_num,

                    previous_process_code = previousTask.process_code,
                    previous_process_name = previousTask.process_name,

                    previous_task_status = previousTask.status,
                    previous_start_time = previousTask.start_time,
                    previous_end_time = previousTask.end_time,

                    is_finished = isFinished,

                    message = isFinished
                        ? $"Order {orderRow.order_id}: công đoạn trước {previousCode} đã Finished."
                        : $"Order {orderRow.order_id}: công đoạn trước {previousCode} chưa Finished."
                });
            }

            return result;
        }

        private async Task<List<string>> GetOrderRouteCodesAsync(
    int orderId,
    CancellationToken ct)
        {
            var processCsv = await _db.order_items
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .OrderBy(x => x.item_id)
                .Select(x => x.production_process)
                .FirstOrDefaultAsync(ct);

            return ParseProcessCodes(processCsv);
        }

        private static string? ResolvePreviousProcessCode(
            List<string> routeCodes,
            string currentProcessCode)
        {
            if (routeCodes == null || routeCodes.Count == 0)
                return null;

            var current = GroupProductionHelper.Norm(currentProcessCode);

            var currentIndex = routeCodes.FindIndex(x =>
                string.Equals(
                    GroupProductionHelper.Norm(x),
                    current,
                    StringComparison.OrdinalIgnoreCase));

            if (currentIndex <= 0)
                return null;

            return routeCodes[currentIndex - 1];
        }

        private sealed class PreviousProcessTaskRef
        {
            public int task_id { get; set; }

            public int? prod_id { get; set; }

            public string? prod_kind { get; set; }

            public int? seq_num { get; set; }

            public string? process_code { get; set; }

            public string? process_name { get; set; }

            public string? status { get; set; }

            public DateTime? start_time { get; set; }

            public DateTime? end_time { get; set; }
        }

        

        private static bool IsPrivateOrderProcess(string? processCode)
        {
            var code = NormProcessCode(processCode);

            return code is "BE" or "DUT" or "DAN";
        }

        private static bool IsDept1(string code)
            => Dept1Codes.Contains(NormProcessCode(code), StringComparer.OrdinalIgnoreCase);

        private static bool IsDept2(string code)
            => Dept2Codes.Contains(NormProcessCode(code), StringComparer.OrdinalIgnoreCase);

        private static bool IsDept3(string code)
            => Dept3Codes.Contains(NormProcessCode(code), StringComparer.OrdinalIgnoreCase);

        private static int FullRouteIndex(string? processCode)
        {
            var code = NormProcessCode(processCode);

            var idx = Array.FindIndex(FullRouteOrder, x =>
                string.Equals(x, code, StringComparison.OrdinalIgnoreCase));

            return idx < 0 ? 999 : idx;
        }

        private static string ResolveDepartmentCode(string processCode)
        {
            var code = NormProcessCode(processCode);

            if (IsDept1(code)) return "DEPT_1";
            if (IsDept2(code)) return "DEPT_2";
            if (IsDept3(code)) return "DEPT_3";

            return "OTHER";
        }

        private static string ResolveDepartmentName(string departmentCode)
        {
            return departmentCode switch
            {
                "DEPT_1" => "Ralo - Cắt - In",
                "DEPT_2" => "Phủ - Cán - Bồi",
                "DEPT_3" => "Bế - Dứt - Dán",
                _ => "Khác"
            };
        }

        private static bool RequiresSameMaterialKey(string processCode)
        {
            var code = NormProcessCode(processCode);

            return code is "PHU" or "CAN" or "BOI";
        }
        private async Task<PreviousProcessTaskRef?> FindPreviousProcessTaskForOrderAsync(
    int orderId,
    string previousProcessCode,
    CancellationToken ct)
        {
            var previousCode = GroupProductionHelper.Norm(previousProcessCode);

            var directTasks = await (
                from t in _db.tasks.AsNoTracking()

                join p in _db.productions.AsNoTracking()
                    on t.prod_id equals p.prod_id

                join pp in _db.product_type_processes.AsNoTracking()
                    on t.process_id equals pp.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where p.order_id == orderId
                      && p.status != "Cancelled"

                select new PreviousProcessTaskRef
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    prod_kind = p.prod_kind,
                    seq_num = t.seq_num,
                    process_code = pp != null ? pp.process_code : null,
                    process_name = pp != null ? pp.process_name : null,
                    status = t.status,
                    start_time = t.start_time,
                    end_time = t.end_time
                }
            ).ToListAsync(ct);

            var matchedDirect = directTasks
                .Where(x => string.Equals(
                    GroupProductionHelper.Norm(x.process_code),
                    previousCode,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => IsTaskFinished(x.status, x.end_time))
                .ThenByDescending(x => x.end_time ?? DateTime.MinValue)
                .ThenByDescending(x => x.task_id)
                .FirstOrDefault();

            if (matchedDirect != null)
                return matchedDirect;

            /*
             * 2. Fallback: nếu không tìm thấy trong direct task,
             * tìm qua task_links để biết công đoạn trước nằm ở GROUP production khác.
             */
            var linkedTasks = await (
                from tl in _db.task_links.AsNoTracking()

                join gt in _db.tasks.AsNoTracking()
                    on tl.group_task_id equals gt.task_id

                join gp in _db.productions.AsNoTracking()
                    on tl.group_prod_id equals gp.prod_id

                join pp in _db.product_type_processes.AsNoTracking()
                    on gt.process_id equals pp.process_id into ppj
                from pp in ppj.DefaultIfEmpty()

                where tl.order_id == orderId
                      && gp.status != "Cancelled"

                select new PreviousProcessTaskRef
                {
                    task_id = gt.task_id,
                    prod_id = gt.prod_id,
                    prod_kind = gp.prod_kind,
                    seq_num = gt.seq_num,
                    process_code = pp != null ? pp.process_code : tl.process_code,
                    process_name = pp != null ? pp.process_name : null,
                    status = gt.status,
                    start_time = gt.start_time,
                    end_time = gt.end_time
                }
            ).ToListAsync(ct);

            var matchedLinked = linkedTasks
                .Where(x => string.Equals(
                    GroupProductionHelper.Norm(x.process_code),
                    previousCode,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => IsTaskFinished(x.status, x.end_time))
                .ThenByDescending(x => x.end_time ?? DateTime.MinValue)
                .ThenByDescending(x => x.task_id)
                .FirstOrDefault();

            if (matchedLinked != null)
                return matchedLinked;

            /*
             * 3. Fallback cuối: task_qtys.
             * Nếu đã có allocation từ GROUP thì coi như công đoạn đó đã Finished.
             */
            var qtyRow = await _db.task_qtys
                .AsNoTracking()
                .Where(x => x.order_id == orderId)
                .ToListAsync(ct);

            var matchedQty = qtyRow
                .Where(x => string.Equals(
                    GroupProductionHelper.Norm(x.process_code),
                    previousCode,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.created_at)
                .FirstOrDefault();

            if (matchedQty != null)
            {
                return new PreviousProcessTaskRef
                {
                    task_id = (int)matchedQty.single_task_id,
                    prod_id = null,
                    prod_kind = "GROUP_QTY",
                    seq_num = null,
                    process_code = matchedQty.process_code,
                    process_name = matchedQty.process_code,
                    status = "Finished",
                    start_time = null,
                    end_time = matchedQty.created_at
                };
            }

            return null;
        }

        private static bool IsTaskFinished(string? status, DateTime? endTime)
        {
            return string.Equals(status, "Finished", StringComparison.OrdinalIgnoreCase)
                   || endTime != null;
        }

        private async Task LinkSingleTasksAsync(
    production groupProd,
    List<task> groupTasks,
    List<SingleRow> rows,
    CancellationToken ct)
        {
            var groupTaskCodes = new Dictionary<string, task>(StringComparer.OrdinalIgnoreCase);

            foreach (var groupTask in groupTasks)
            {
                var code = await _db.product_type_processes
                    .Where(x => x.process_id == groupTask.process_id)
                    .Select(x => x.process_code)
                    .FirstAsync(ct);

                groupTaskCodes[GroupProductionHelper.Norm(code)] = groupTask;
            }

            var singleProdIds = rows.Select(x => x.SingleProdId).Distinct().ToList();

            var singleTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id.HasValue && singleProdIds.Contains(x.prod_id.Value))
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                foreach (var kv in groupTaskCodes)
                {
                    var code = kv.Key;
                    var groupTask = kv.Value;

                    var singleTask = singleTasks.FirstOrDefault(x =>
                        x.prod_id == row.SingleProdId &&
                        string.Equals(
                            GroupProductionHelper.Norm(x.process?.process_code),
                            code,
                            StringComparison.OrdinalIgnoreCase));

                    if (singleTask == null)
                    {
                        throw new InvalidOperationException(
                            $"Production riêng {row.SingleProdId} không có task công đoạn {code}.");
                    }

                    if (!string.Equals(singleTask.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    {
                        singleTask.status = "GroupedWaiting";
                        singleTask.reason = $"Đã ghép vào production chung prod_id={groupProd.prod_id}.";
                    }

                    await _db.task_links.AddAsync(new task_link
                    {
                        group_prod_id = groupProd.prod_id,
                        group_task_id = groupTask.task_id,
                        single_prod_id = row.SingleProdId,
                        single_task_id = singleTask.task_id,
                        order_id = row.OrderId,
                        process_code = code,
                        qty_plan = row.Qty,
                        status = "Waiting",
                        created_at = AppTime.NowVnUnspecified()
                    }, ct);
                }
            }
        }

        private static (
            List<GroupStageMaterialDto> inputs,
            List<GroupStageMaterialDto> outputs,
            decimal estimatedOutputQty)
            BuildGroupStageIO(
                string? processCode,
                string? processName,
                int groupTotalQty,
                decimal previousOutputQty,
                string previousOutputName,
                List<GroupTaskLogDto> logs)
        {
            var code = GroupProductionHelper.Norm(processCode);
            var estimatedQty = groupTotalQty > 0 ? groupTotalQty : previousOutputQty;

            var inputs = new List<GroupStageMaterialDto>
        {
            new()
            {
                code = "PREV",
                name = previousOutputName,
                unit = code is "BE" or "DUT" or "DAN" ? "sp" : "tờ",
                estimated_qty = previousOutputQty,
                actual_qty = ResolveActualReferenceInput(logs, previousOutputQty)
            }
        };

            if (code == "PHU")
            {
                inputs.Add(new GroupStageMaterialDto
                {
                    code = "COATING",
                    name = "Keo/phủ nhập tay",
                    unit = "kg",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "COATING")
                });
            }
            else if (code is "CAN" or "CAN_MANG")
            {
                inputs.Add(new GroupStageMaterialDto
                {
                    code = "LAMINATION",
                    name = "Màng cán nhập tay",
                    unit = "kg",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "LAMINATION")
                });
            }
            else if (code == "BOI")
            {
                inputs.Add(new GroupStageMaterialDto
                {
                    code = "WAVE",
                    name = "Sóng carton nhập tay",
                    unit = "tờ",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "WAVE")
                });

                inputs.Add(new GroupStageMaterialDto
                {
                    code = "KEO_BOI",
                    name = "Keo bồi nhập tay",
                    unit = "kg",
                    estimated_qty = 0,
                    actual_qty = ResolveActualMaterial(logs, "KEO_BOI")
                });
            }

            var outputName = code switch
            {
                "PHU" => "BTP sau phủ",
                "CAN" => "BTP sau cán",
                "CAN_MANG" => "BTP sau cán màng",
                "BOI" => "BTP sau bồi",
                "BE" => "BTP sau bế",
                "DUT" => "BTP sau dứt",
                "DAN" => "Thành phẩm sau dán",
                _ => $"BTP sau {processName ?? processCode}"
            };

            var actualOutput = logs.Sum(x => x.qty_good);

            var outputs = new List<GroupStageMaterialDto>
        {
            new()
            {
                code = code,
                name = outputName,
                unit = code is "BE" or "DUT" or "DAN" ? "sp" : "tờ",
                estimated_qty = estimatedQty,
                actual_qty = actualOutput
            }
        };

            return (inputs, outputs, estimatedQty);
        }

        private static decimal ResolveActualReferenceInput(
            List<GroupTaskLogDto> logs,
            decimal fallback)
        {
            var json = logs
                .OrderByDescending(x => x.log_time)
                .Select(x => x.reference_input_json)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(json))
                return 0;

            try
            {
                var refs = JsonSerializer.Deserialize<List<TaskReferenceUsageInputDto>>(json, JsonOptions)
                           ?? new List<TaskReferenceUsageInputDto>();

                return refs.Sum(x => x.quantity_used);
            }
            catch
            {
                return 0;
            }
        }

        private static decimal ResolveActualMaterial(
            List<GroupTaskLogDto> logs,
            string code)
        {
            var json = logs
                .OrderByDescending(x => x.log_time)
                .Select(x => x.material_usage_json)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(json))
                return 0;

            try
            {
                var mats = JsonSerializer.Deserialize<List<TaskMaterialUsageLogItemDto>>(json, JsonOptions)
                           ?? new List<TaskMaterialUsageLogItemDto>();

                return mats
                    .Where(x => string.Equals(x.material_code, code, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.quantity_used);
            }
            catch
            {
                return 0;
            }
        }

        private sealed class SingleRow
        {
            public int OrderId { get; set; }
            public int SingleProdId { get; set; }
            public int Qty { get; set; }
        }

        private sealed class GroupOrderRow
        {
            public order Order { get; init; } = null!;
            public production SingleProd { get; init; } = null!;
            public order_item Item { get; init; } = null!;
            public order_request? Request { get; init; }
            public cost_estimate? Estimate { get; init; }
            public List<string> RouteCodes { get; init; } = new();
        }

        private sealed class ProductionPlanSegment
        {
            public string DepartmentCode { get; init; } = "";
            public string DepartmentName { get; init; } = "";

            public List<string> ProcessCodes { get; set; } = new();

            public List<GroupOrderRow> Members { get; set; } = new();

            public string? MaterialKey { get; set; }

            public bool IsGroup => Members.Count >= 2;
        }

        private static string NormProcessCode(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static List<string> ParseProcessCodes(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int DepartmentOrder(string departmentCode)
        {
            return departmentCode switch
            {
                "DEPT_1" => 1,
                "DEPT_2" => 2,
                "DEPT_3" => 3,
                _ => 99
            };
        }

        private static string SafeKey(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "NULL"
                : NormProcessCode(value);
        }

        private string ResolveMaterialGroupKey(string processCode, GroupOrderRow row)
        {
            var code = NormProcessCode(processCode);

            if (code == "PHU")
            {
                var coating = row.Estimate?.coating_type;

                return $"PHU:COATING={SafeKey(coating)}";
            }

            if (code == "CAN")
            {
                var lamination =
                    !string.IsNullOrWhiteSpace(row.Estimate?.lamination_material_code)
                        ? row.Estimate!.lamination_material_code
                        : !string.IsNullOrWhiteSpace(row.Estimate?.lamination_material_name)
                            ? row.Estimate!.lamination_material_name
                            : row.Estimate?.lamination_material_id?.ToString();

                return $"CAN:LAMINATION={SafeKey(lamination)}";
            }

            if (code == "BOI")
            {
                var wave = EstimateMaterialAlternativeHelper.ResolveWaveType(
                    row.Estimate?.wave_alternative,
                    row.Estimate?.wave_type);

                return $"BOI:WAVE={SafeKey(wave)}";
            }

            return $"{code}:NO_MATERIAL_CHECK";
        }

        private async Task<List<GroupOrderRow>> LoadGroupOrderRowsAsync(
    List<int> orderIds,
    CancellationToken ct)
        {
            var rows = await (
                from o in _db.orders
                join pr in _db.productions on o.order_id equals pr.order_id
                where orderIds.Contains(o.order_id)
                      && pr.prod_kind == "SINGLE"
                select new
                {
                    order = o,
                    singleProd = pr,
                    item = _db.order_items
                        .Where(i => i.order_id == o.order_id)
                        .OrderBy(i => i.item_id)
                        .FirstOrDefault()
                }
            ).ToListAsync(ct);

            var result = new List<GroupOrderRow>();

            foreach (var row in rows)
            {
                if (row.item == null)
                    continue;

                var req = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == row.order.order_id)
                    .OrderByDescending(x => x.order_request_id)
                    .FirstOrDefaultAsync(ct);

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

                result.Add(new GroupOrderRow
                {
                    Order = row.order,
                    SingleProd = row.singleProd,
                    Item = row.item,
                    Request = req,
                    Estimate = est,
                    RouteCodes = ParseProcessCodes(row.item.production_process)
                });
            }

            return result;
        }

        private List<ProductionPlanSegment> BuildDepartmentProductionPlan(
    List<GroupOrderRow> rows,
    List<string> selectedCodes,
    out List<GroupProductionPlanWarningDto> warnings)
        {
            warnings = new List<GroupProductionPlanWarningDto>();

            var normalizedSelectedCodes = selectedCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            /*
             * RALO/CAT/IN.
             * Các công đoạn này nằm ở production SINGLE của từng order.
             */
            var nonDept1Codes = normalizedSelectedCodes
                .Where(x => !IsDept1(x))
                .ToList();

            var sharedProcessCodes = nonDept1Codes
                .Where(x => !IsPrivateOrderProcess(x))
                .ToList();

            var privateOrderProcessCodes = nonDept1Codes
                .Where(IsPrivateOrderProcess)
                .ToList();

            var result = new List<ProductionPlanSegment>();

            /*
             * 1. PHU / CAN / BOI:
             * Vẫn có thể sản xuất GROUP nếu cùng material_key.
             */
            foreach (var processCode in sharedProcessCodes)
            {
                var deptCode = ResolveDepartmentCode(processCode);
                var deptName = ResolveDepartmentName(deptCode);

                var membersWithProcess = rows
                    .Where(r => r.RouteCodes.Contains(processCode, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (membersWithProcess.Count == 0)
                    continue;

                if (RequiresSameMaterialKey(processCode))
                {
                    var materialGroups = membersWithProcess
                        .GroupBy(x => ResolveMaterialGroupKey(processCode, x))
                        .ToList();

                    if (materialGroups.Count > 1)
                    {
                        warnings.Add(new GroupProductionPlanWarningDto
                        {
                            process_code = processCode,
                            reason = $"Công đoạn {processCode} khác mã vật tư/material_key nên không thể ghép chung tất cả order. Hệ thống tự tách theo từng nhóm vật tư.",
                            affected_order_ids = membersWithProcess
                                .Select(x => x.Order.order_id)
                                .Distinct()
                                .OrderBy(x => x)
                                .ToList(),
                            material_groups = materialGroups.ToDictionary(
                                g => g.Key,
                                g => g.Select(x => x.Order.order_id)
                                      .Distinct()
                                      .OrderBy(x => x)
                                      .ToList())
                        });
                    }

                    foreach (var mg in materialGroups)
                    {
                        result.Add(new ProductionPlanSegment
                        {
                            DepartmentCode = deptCode,
                            DepartmentName = deptName,
                            ProcessCodes = new List<string> { processCode },
                            Members = mg.ToList(),
                            MaterialKey = null
                        });
                    }
                }
                else
                {
                    result.Add(new ProductionPlanSegment
                    {
                        DepartmentCode = deptCode,
                        DepartmentName = deptName,
                        ProcessCodes = new List<string> { processCode },
                        Members = membersWithProcess,
                        MaterialKey = null
                    });
                }
            }

            /*
             * 2. BE / DUT / DAN:
             * Không tạo GROUP giữa nhiều order.
             * Mỗi order tạo riêng, gom BE/DUT/DAN của order đó vào cùng 1 production nếu có đủ path.
             */
            if (privateOrderProcessCodes.Count > 0)
            {
                foreach (var row in rows.OrderBy(x => x.Order.order_id))
                {
                    var privateCodesForOrder = privateOrderProcessCodes
                        .Where(code => row.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(FullRouteIndex)
                        .ToList();

                    if (privateCodesForOrder.Count == 0)
                        continue;

                    result.Add(new ProductionPlanSegment
                    {
                        DepartmentCode = "DEPT_3",
                        DepartmentName = ResolveDepartmentName("DEPT_3"),
                        ProcessCodes = privateCodesForOrder,
                        Members = new List<GroupOrderRow> { row },
                        MaterialKey = $"ORDER:{row.Order.order_id}"
                    });
                }
            }

            return MergeAdjacentSegments(result);
        }

        private static int GetGlobalRouteIndex(
            List<GroupOrderRow> rows,
            string processCode)
        {
            var indexes = rows
                .Select(r => r.RouteCodes.FindIndex(x =>
                    string.Equals(x, processCode, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x >= 0)
                .ToList();

            return indexes.Count == 0 ? 999 : indexes.Min();
        }

        private static string ShortDepartmentCode(string? departmentCode)
        {
            var code = (departmentCode ?? "").Trim().ToUpperInvariant();

            return code switch
            {
                "DEPT_1" => "D1",
                "DEPT_2" => "D2",
                "DEPT_3" => "D3",
                _ => "DX"
            };
        }

        private async Task<string> GenerateShortProductionCodeAsync(
            string prefix,
            string? departmentCode,
            CancellationToken ct)
        {
            var safePrefix = (prefix ?? "PRD")
                .Trim()
                .ToUpperInvariant();

            if (safePrefix.Length > 3)
                safePrefix = safePrefix[..3];

            if (safePrefix.Length < 3)
                safePrefix = safePrefix.PadRight(3, 'X');

            var dept = ShortDepartmentCode(departmentCode);

            for (var i = 0; i < 20; i++)
            {
                var now = AppTime.NowVnUnspecified();

                var code = $"{safePrefix}{dept}{now:MMddHHmmss}{Random.Shared.Next(100, 999)}";

                if (code.Length > 20)
                    code = code[..20];

                var exists = await _db.productions
                    .AsNoTracking()
                    .AnyAsync(x => x.code == code, ct);

                if (!exists)
                    return code;
            }

            var fallback = $"{safePrefix}{dept}{Random.Shared.Next(100000000, 999999999)}";

            return fallback.Length <= 20
                ? fallback
                : fallback[..20];
        }

        private static List<ProductionPlanSegment> MergeAdjacentSegments(
    List<ProductionPlanSegment> segments)
        {
            var result = new List<ProductionPlanSegment>();

            foreach (var seg in segments)
            {
                var last = result.LastOrDefault();

                if (last != null &&
                    last.DepartmentCode == seg.DepartmentCode &&
                    SameMembers(last.Members, seg.Members) &&
                    CanMergeSegmentKey(last.MaterialKey, seg.MaterialKey))
                {
                    var mergedCodes = last.ProcessCodes
                        .Concat(seg.ProcessCodes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList();

                    last.ProcessCodes = mergedCodes;

                    if (string.IsNullOrWhiteSpace(last.MaterialKey))
                        last.MaterialKey = seg.MaterialKey;

                    continue;
                }

                result.Add(new ProductionPlanSegment
                {
                    DepartmentCode = seg.DepartmentCode,
                    DepartmentName = seg.DepartmentName,
                    ProcessCodes = seg.ProcessCodes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),
                    Members = seg.Members.ToList(),
                    MaterialKey = seg.MaterialKey
                });
            }

            return result;
        }

        private static bool SameMembers(
            List<GroupOrderRow> a,
            List<GroupOrderRow> b)
        {
            var aa = a.Select(x => x.Order.order_id).OrderBy(x => x).ToList();
            var bb = b.Select(x => x.Order.order_id).OrderBy(x => x).ToList();

            return aa.SequenceEqual(bb);
        }

        private static bool CanMergeSegmentKey(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return true;

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanMergeMaterialKey(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return true;

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
