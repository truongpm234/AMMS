using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Productions.Groups;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AMMS.Application.Services
{
    public class GroupProductionService : IGroupProductionService
    {
        private readonly AppDbContext _db;
        private readonly NotificationService _noti;
        private readonly IHubContext<RealtimeHub> _hub;
        private static readonly string[] Dept1Codes = { "RALO", "CAT", "IN" };
        private static readonly string[] Dept2Codes = { "PHU", "CAN", "CAN_MANG", "BOI" };
        private static readonly string[] Dept3Codes = { "BE", "DUT", "DAN" };
        private static readonly string[] FullRouteOrder = { "RALO", "CAT", "IN", "PHU", "CAN", "CAN_MANG", "BOI", "BE", "DUT", "DAN" };
        private const int MinProductionDays = 7;
        private const int Dept1Days = 3;
        private const int Dept2Days = 2;
        private const int Dept3Days = 2;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public GroupProductionService(AppDbContext db, NotificationService noti, IHubContext<RealtimeHub> hub)
        {
            _db = db;
            _noti = noti;
            _hub = hub;
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

            /*
             * FIX:
             * Không dùng .Select(GroupProductionHelper.Norm)
             * vì nếu FE gửi ["PHU,CAN"] thì sẽ thành 1 code "PHU,CAN".
             */
            var selectedCodes = req.process_codes
                .SelectMany(x => GroupProductionHelper.ParseCodes(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Cần chọn ít nhất 1 công đoạn để tạo lệnh sản xuất ghép/tách.");

            GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            /*
             * Preview dùng cùng body request.
             * Nếu req.planned_start_date null thì preview tự tính suggested_planned_start_date.
             */
            var preview = await PreviewAsync(req, ct);

            if (preview.days_late_if_any > 0)
            {
                throw new InvalidOperationException(
                    $"Lịch ghép dự kiến trễ {preview.days_late_if_any} ngày so với mốc giao chung {preview.common_delivery_deadline:yyyy-MM-dd}.");
            }

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

                var allSteps = await _db.product_type_processes
                    .Where(x => x.product_type_id == productTypeId && (x.is_active ?? true))
                    .OrderBy(x => x.seq_num)
                    .ToListAsync(ct);

                var createdGroupProdIds = new List<int>();
                var createdSplitProdIds = new List<int>();

                foreach (var segment in plan)
                {
                    var segmentStart = ResolveStageStart(preview, segment);
                    var segmentEnd = ResolveStageEnd(preview, segment);

                    if (segment.IsGroup)
                    {
                        var groupProd = await CreateDepartmentGroupProductionAsync(
                            segment,
                            productTypeId,
                            managerUserId,
                            segmentStart,
                            segmentEnd,
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
                            segmentStart,
                            segmentEnd,
                            req.note,
                            allSteps,
                            ct);

                        createdSplitProdIds.Add(splitProd.prod_id);
                    }
                }

                /*
                 * FIX:
                 * Function này đã có nhưng code hiện tại chưa gọi.
                 * Phải gọi để các task Dept1 còn lại trong SINGLE có planned_start_time/planned_end_time.
                 */
                await SyncSingleDept1TaskTimelineAsync(
                    rows,
                    preview,
                    ct);

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
    DateTime plannedEnd,
    string? note,
    List<product_type_process> allSteps,
    CancellationToken ct)
        {
            if (segment == null)
                throw new InvalidOperationException("segment is required.");

            if (segment.Members == null || segment.Members.Count < 2)
                throw new InvalidOperationException("Production ghép cần ít nhất 2 order.");

            var now = AppTime.NowVnUnspecified();

            var selectedCodes = segment.ProcessCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("Không có process_code hợp lệ để tạo production ghép.");

            var codesCsv = string.Join(",", selectedCodes);

            var groupCode = await GenerateShortProductionCodeAsync(
                "GRP",
                segment.DepartmentCode,
                ct);

            var inheritedProdMethod = ResolveSegmentProductionMethodOrThrow(segment);

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
                prod_method = inheritedProdMethod,
                group_process_codes = codesCsv,
                group_total_qty = segment.Members.Sum(x => x.Item.quantity)
            };

            await _db.productions.AddAsync(groupProd, ct);
            await _db.SaveChangesAsync(ct);

            var prod_id = await _db.productions.FirstOrDefaultAsync(o => o.code == groupProd.code);
            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager")).SendAsync("group-production", new { meassage = $"Lệnh sản xuất {prod_id.prod_id} đã được lên lịch" });
            await _noti.CreateNotfi(6, $"Lệnh sản xuất {prod_id} đã được lên lịch", null, prod_id.prod_id, "Inprocessing");
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

            await _db.SaveChangesAsync(ct);

            var stepRows = allSteps
                .Where(x => selectedCodes.Contains(
                    NormProcessCode(x.process_code),
                    StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => FullRouteIndex(x.process_code))
                .ToList();

            if (stepRows.Count != selectedCodes.Count)
            {
                var foundCodes = stepRows
                    .Select(x => NormProcessCode(x.process_code))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missing = selectedCodes
                    .Where(x => !foundCodes.Contains(x))
                    .ToList();

                throw new InvalidOperationException(
                    $"Không tìm thấy đủ process trong product_type_processes. Thiếu: {string.Join(",", missing)}");
            }

            var memberBySingleProdId = segment.Members
                .GroupBy(x => x.SingleProd.prod_id)
                .ToDictionary(
                    g => g.Key,
                    g => g.First());

            var singleProdIds = memberBySingleProdId.Keys.ToList();

            var allSingleTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x =>
                    x.prod_id.HasValue &&
                    singleProdIds.Contains(x.prod_id.Value))
                .OrderBy(x => x.prod_id)
                .ThenBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            var tasksToDelete = allSingleTasks
                .Where(x =>
                    selectedCodes.Contains(
                        NormProcessCode(x.process?.process_code),
                        StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var member in segment.Members)
            {
                foreach (var code in selectedCodes)
                {
                    var hasTask = tasksToDelete.Any(x =>
                        x.prod_id == member.SingleProd.prod_id &&
                        string.Equals(
                            NormProcessCode(x.process?.process_code),
                            code,
                            StringComparison.OrdinalIgnoreCase));

                    if (!hasTask)
                    {
                        throw new InvalidOperationException(
                            $"Không tìm thấy task công đoạn {code} trong production single {member.SingleProd.prod_id} của order {member.Order.order_id}.");
                    }
                }
            }

            await ValidateSingleTasksCanBeDeletedForGroupAsync(
                tasksToDelete,
                allSingleTasks,
                ct);

            var groupTaskByCode = new Dictionary<string, task>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in stepRows)
            {
                var code = NormProcessCode(step.process_code);

                var groupTask = new task
                {
                    prod_id = groupProd.prod_id,
                    name = $"GROUP-{segment.DepartmentCode}-{step.process_name ?? step.process_code}",

                    // Quan trọng:
                    // Không dùng seq = 1,2,3 local.
                    // Dùng seq_num gốc để dependency biết PHU đứng sau IN, CAN đứng sau PHU...
                    seq_num = step.seq_num,

                    status = "Unassigned",
                    machine = ResolveTaskMachineFromProcess(step),
                    process_id = step.process_id,
                    input_mode = "MANUAL",

                    planned_start_time = plannedStart,
                    planned_end_time = plannedStart.AddDays(ResolveDepartmentDurationDays(segment.DepartmentCode)),

                    reason = $"Task thuộc production ghép phòng ban {segment.DepartmentName}, nhập tay input/output khi báo cáo."
                };

                await _db.tasks.AddAsync(groupTask, ct);
                groupTaskByCode[code] = groupTask;
            }

            await _db.SaveChangesAsync(ct);

            foreach (var singleTask in tasksToDelete)
            {
                if (!singleTask.prod_id.HasValue)
                    throw new InvalidOperationException($"Task {singleTask.task_id} thiếu prod_id.");

                var singleProdId = singleTask.prod_id.Value;

                if (!memberBySingleProdId.TryGetValue(singleProdId, out var member))
                    throw new InvalidOperationException($"Không tìm thấy member của single_prod_id={singleProdId}.");

                var processCode = NormProcessCode(singleTask.process?.process_code);

                if (!groupTaskByCode.TryGetValue(processCode, out var groupTask))
                    throw new InvalidOperationException($"Không tìm thấy group task cho process_code={processCode}.");

                await _db.task_links.AddAsync(new task_link
                {
                    group_prod_id = groupProd.prod_id,
                    group_task_id = groupTask.task_id,

                    single_prod_id = singleProdId,

                    // Quan trọng:
                    // Task single sẽ bị xóa nên không được giữ FK tới task này.
                    single_task_id = null,

                    // Chỉ lưu để trace/debug.
                    original_single_task_id = singleTask.task_id,

                    order_id = member.Order.order_id,
                    process_code = processCode,
                    qty_plan = member.Item.quantity,

                    status = "Active",
                    created_at = now
                }, ct);
            }

            _db.tasks.RemoveRange(tasksToDelete);

            await _db.SaveChangesAsync(ct);

            return groupProd;
        }

        private async Task ValidateSingleTasksCanBeDeletedForGroupAsync(
    List<task> tasksToDelete,
    List<task> allSingleTasks,
    CancellationToken ct)
        {
            if (tasksToDelete == null || tasksToDelete.Count == 0)
                throw new InvalidOperationException("Không có task single nào để xóa khi tạo production ghép.");

            var taskIdsToDelete = tasksToDelete
                .Select(x => x.task_id)
                .Distinct()
                .ToList();

            var taskIdDeleteSet = taskIdsToDelete.ToHashSet();

            var allTaskIds = allSingleTasks
                .Select(x => x.task_id)
                .Distinct()
                .ToList();

            var taskIdsWithLogs = allTaskIds.Count == 0
                ? new HashSet<int>()
                : (await _db.task_logs
                    .AsNoTracking()
                    .Where(x =>
                        x.task_id.HasValue &&
                        allTaskIds.Contains(x.task_id.Value))
                    .Select(x => x.task_id!.Value)
                    .Distinct()
                    .ToListAsync(ct))
                    .ToHashSet();

            var alreadyLinkedTaskIds = await _db.task_links
    .AsNoTracking()
    .Where(x =>
        (
            x.single_task_id.HasValue &&
            taskIdsToDelete.Contains(x.single_task_id.Value)
        )
        ||
        (
            x.original_single_task_id.HasValue &&
            taskIdsToDelete.Contains(x.original_single_task_id.Value)
        ))
    .Where(x =>
        x.status == null ||
        x.status.ToUpper() != "CANCELLED")
    .Select(x => x.original_single_task_id ?? x.single_task_id)
    .ToListAsync(ct);

            if (alreadyLinkedTaskIds.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Có task đã được ghép trước đó: {string.Join(",", alreadyLinkedTaskIds.Where(x => x.HasValue).Select(x => x!.Value))}");
            }

            foreach (var t in tasksToDelete)
            {
                var code = NormProcessCode(t.process?.process_code);

                if (taskIdsWithLogs.Contains(t.task_id) ||
                    string.Equals(t.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.status, "InProcessing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    t.start_time != null ||
                    t.end_time != null)
                {
                    throw new InvalidOperationException(
                        $"Không thể ghép/xóa công đoạn {code} của production {t.prod_id} vì task {t.task_id} đã bắt đầu, đã Finished hoặc đã có log.");
                }

                if (!t.prod_id.HasValue || !t.seq_num.HasValue)
                    throw new InvalidOperationException($"Task {t.task_id} thiếu prod_id hoặc seq_num.");

                var laterStarted = allSingleTasks.Any(x =>
                    x.prod_id == t.prod_id &&
                    x.task_id != t.task_id &&
                    !taskIdDeleteSet.Contains(x.task_id) &&
                    x.seq_num.HasValue &&
                    x.seq_num.Value > t.seq_num.Value &&
                    (
                        taskIdsWithLogs.Contains(x.task_id) ||
                        string.Equals(x.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.status, "InProcessing", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                        x.start_time != null ||
                        x.end_time != null
                    ));

                if (laterStarted)
                {
                    throw new InvalidOperationException(
                        $"Không thể ghép/xóa task {t.task_id} của production {t.prod_id} vì công đoạn phía sau đã bắt đầu hoặc đã có log.");
                }
            }
        }

        private static int ResolveDepartmentDurationDays(string? departmentCode)
        {
            return NormProcessCode(departmentCode) switch
            {
                "DEPT_1" => 3,
                "DEPT_2" => 2,
                "DEPT_3" => 2,
                _ => 1
            };
        }

        private static string ResolveTaskMachineFromProcess(product_type_process step)
        {
            if (!string.IsNullOrWhiteSpace(step.machine))
                return step.machine.Trim();

            if (!string.IsNullOrWhiteSpace(step.process_code))
                return step.process_code.Trim().ToUpperInvariant();

            return "";
        }

        private async Task<production> CreateSplitProductionAsync(
    ProductionPlanSegment segment,
    int productTypeId,
    int? managerUserId,
    DateTime plannedStart,
    DateTime plannedEnd,
    string? note,
    List<product_type_process> allSteps,
    CancellationToken ct)
        {
            var member = segment.Members.First();
            var inheritedProdMethod = ResolveSegmentProductionMethodOrThrow(segment);
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
                prod_method = inheritedProdMethod,
                group_process_codes = codesCsv,
                group_total_qty = member.Item.quantity,
                end_date = plannedEnd
            };

            await _hub.Clients.Group(RealtimeGroups.ByRole("production manager")).SendAsync("group-production", new { meassage = $"Lệnh sản xuất {splitProd.prod_id} đã được lên lịch" });
            await _noti.CreateNotfi(6, $"Lệnh sản xuất {splitProd.prod_id} đã được lên lịch", null, splitProd.prod_id, "Inprocessing");

            await _db.productions.AddAsync(splitProd, ct);
            await _db.SaveChangesAsync(ct);

            var processSeqMap = segment.ProcessCodes
                .Select((code, index) => new
                {
                    code = NormProcessCode(code),
                    seq = index + 1
                })
                .ToDictionary(x => x.code, x => x.seq, StringComparer.OrdinalIgnoreCase);

            var existingTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id == member.SingleProd.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            var candidateTasks = existingTasks
                .Where(x =>
                {
                    var code = NormProcessCode(x.process?.process_code);
                    return processSeqMap.ContainsKey(code);
                })
                .ToList();

            var candidateTaskIds = candidateTasks
                .Select(x => x.task_id)
                .ToList();

            var taskIdsWithLogs = candidateTaskIds.Count == 0
                ? new HashSet<int>()
                : (await _db.task_logs
                    .AsNoTracking()
                    .Where(x => x.task_id.HasValue &&
                                candidateTaskIds.Contains(x.task_id.Value))
                    .Select(x => x.task_id!.Value)
                    .Distinct()
                    .ToListAsync(ct))
                    .ToHashSet();

            foreach (var task in candidateTasks)
            {
                var code = NormProcessCode(task.process?.process_code);

                if (taskIdsWithLogs.Contains(task.task_id) ||
                    string.Equals(task.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(task.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    task.start_time != null ||
                    task.end_time != null)
                {
                    throw new InvalidOperationException(
                        $"Không thể tách công đoạn {code} của production {member.SingleProd.prod_id} sang SPLIT vì task đã bắt đầu hoặc đã có log.");
                }
            }

            var splitTasks = new List<task>();

            /*
             * Case 1:
             * SINGLE đã có task tương ứng thì move sang SPLIT.
             */
            foreach (var task in candidateTasks)
            {
                var taskCode = NormProcessCode(task.process?.process_code);

                task.prod_id = splitProd.prod_id;
                task.seq_num = processSeqMap[taskCode];
                task.status = "Unassigned";
                task.start_time = null;
                task.end_time = null;
                task.reason = $"Task được tách sang production {splitProd.code} theo phòng ban {segment.DepartmentName}.";

                splitTasks.Add(task);
            }

            /*
             * Case 2:
             * Nếu không có task để move thì tạo task mới.
             * Lưu ý: set timeline trực tiếp trên splitTasks, không query DB trước SaveChanges.
             */
            if (splitTasks.Count == 0)
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

                    var newTask = new task
                    {
                        prod_id = splitProd.prod_id,
                        name = $"SPLIT-{step.process_name ?? step.process_code}",
                        seq_num = processSeqMap.TryGetValue(code, out var seq) ? seq : 999,
                        status = "Unassigned",
                        machine = ResolveTaskMachineFromProcess(step),
                        process_id = step.process_id,
                        input_mode = "MANUAL",
                        reason = $"Task SPLIT theo phòng ban {segment.DepartmentName}."
                    };

                    await _db.tasks.AddAsync(newTask, ct);
                    splitTasks.Add(newTask);
                }
            }

            if (splitTasks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Không tạo được task SPLIT cho order {member.Order.order_id}, process={codesCsv}.");
            }

            var orderedSplitTasks = splitTasks
                .OrderBy(x => x.seq_num ?? int.MaxValue)
                .ThenBy(x => x.task_id)
                .ToList();

            var taskCount = Math.Max(orderedSplitTasks.Count, 1);
            var totalMinutes = Math.Max(1, (int)(plannedEnd - plannedStart).TotalMinutes);
            var minutesPerTask = Math.Max(1, totalMinutes / taskCount);

            for (var i = 0; i < orderedSplitTasks.Count; i++)
            {
                orderedSplitTasks[i].planned_start_time =
                    plannedStart.AddMinutes(minutesPerTask * i);

                orderedSplitTasks[i].planned_end_time =
                    i == orderedSplitTasks.Count - 1
                        ? plannedEnd
                        : plannedStart.AddMinutes(minutesPerTask * (i + 1));
            }

            await _db.SaveChangesAsync(ct);

            return splitProd;
        }

        public async Task<List<SuggestedGroupProductionDto>> SuggestAsync(
    int? productTypeId,
    string? processCodes,
    string? orderIds,
    CancellationToken ct = default)
        {
            /*
             * Mục tiêu:
             * - Không cần FE truyền orderIds/productType/processCodes.
             * - Nếu không truyền gì: tự lấy toàn bộ order hợp lệ từ GetCandidatesAsync.
             * - Nếu có nhiều product_type khác nhau: tự tách từng product_type rồi suggest riêng.
             * - Trong mỗi product_type:
             *   + Dept1: RALO/CAT/IN không suggest group.
             *   + Dept2: PHU/CAN/CAN_MANG/BOI có thể GROUP nếu đủ điều kiện.
             *   + Dept3: BE/DUT/DAN không GROUP nhiều order, chỉ auto SPLIT sau GROUP Dept2.
             */

            var selectedCodes = GroupProductionHelper.ParseCodes(processCodes);
            var selectedOrderIds = ParseOrderIdsCsv(orderIds);

            if (selectedCodes.Count > 0)
                GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            List<GroupOrderRow> allRows;

            /*
             * CASE 1:
             * FE có truyền orderIds thì chỉ suggest trong danh sách order đó.
             */
            if (selectedOrderIds.Count > 0)
            {
                allRows = await LoadGroupOrderRowsAsync(selectedOrderIds, ct);

                if (allRows.Count != selectedOrderIds.Count)
                {
                    var found = allRows
                        .Select(x => x.Order.order_id)
                        .ToHashSet();

                    var missing = selectedOrderIds
                        .Where(x => !found.Contains(x))
                        .ToList();

                    throw new InvalidOperationException(
                        $"Không tìm thấy đủ order hoặc production SINGLE để gợi ý. Missing: {string.Join(",", missing)}");
                }
            }
            /*
             * CASE 2:
             * FE không truyền gì.
             * Backend tự lấy tất cả order có thể ghép.
             */
            else
            {
                var candidateProcessFilter = selectedCodes.Count > 0
                    ? processCodes
                    : null;

                var candidates = await GetCandidatesAsync(
                    productTypeId,
                    candidateProcessFilter,
                    ct);

                var candidateOrderIds = candidates
                    .Where(x => x.can_group)
                    .Select(x => x.order_id)
                    .Distinct()
                    .ToList();

                if (candidateOrderIds.Count < 2)
                    return new List<SuggestedGroupProductionDto>();

                allRows = await LoadGroupOrderRowsAsync(candidateOrderIds, ct);
            }

            /*
             * Nếu API vẫn truyền productTypeId thì lọc lại thêm 1 lần.
             */
            if (productTypeId.HasValue)
            {
                allRows = allRows
                    .Where(x => x.Item.product_type_id == productTypeId.Value)
                    .ToList();
            }

            allRows = allRows
                .Where(x => x.Item != null)
                .Where(x => x.Item.product_type_id.HasValue)
                .Where(x => x.Order.layout_confirmed)
                .Where(x => x.Order.is_production_ready)
                .Where(x => string.Equals(x.Order.status, "Scheduled", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allRows.Count < 2)
                return new List<SuggestedGroupProductionDto>();

            /*
             * Lấy tên product_type để trả ra response.
             */
            var productTypeIds = allRows
                .Where(x => x.Item.product_type_id.HasValue)
                .Select(x => x.Item.product_type_id!.Value)
                .Distinct()
                .ToList();

            var productTypeNameMap = await _db.product_types
                .AsNoTracking()
                .Where(x => productTypeIds.Contains(x.product_type_id))
                .ToDictionaryAsync(
                    x => x.product_type_id,
                    x => x.name,
                    ct);

            var finalSuggestions = new List<SuggestedGroupProductionDto>();

            /*
             * QUAN TRỌNG:
             * Tự tách theo product_type_id.
             * Như vậy nhiều product type khác nhau vẫn suggest được,
             * nhưng không suggest ghép chéo sai product type.
             */
            foreach (var productTypeGroup in allRows
                         .GroupBy(x => x.Item.product_type_id!.Value)
                         .OrderBy(g => g.Key))
            {
                var rowsOfOneProductType = productTypeGroup
                    .OrderBy(x => x.Order.delivery_date)
                    .ThenBy(x => x.Order.order_id)
                    .ToList();

                if (rowsOfOneProductType.Count < 2)
                    continue;

                var currentProductTypeId = productTypeGroup.Key;

                productTypeNameMap.TryGetValue(
                    currentProductTypeId,
                    out var currentProductTypeName);

                List<SuggestedGroupProductionDto> suggestionsOfType;

                /*
                 * Nếu FE có truyền processCodes:
                 * suggest đúng theo process FE chọn, nhưng vẫn chia riêng từng product_type.
                 */
                if (selectedCodes.Count > 0)
                {
                    suggestionsOfType = BuildSuggestionPreviewFromSelectedCodes(
                        rowsOfOneProductType,
                        selectedCodes);
                }
                /*
                 * Nếu FE không truyền gì:
                 * tự suggest tất cả nhóm Dept2 có thể ghép.
                 */
                else
                {
                    suggestionsOfType = BuildAutoDept2Suggestions(
                        rowsOfOneProductType);
                }

                foreach (var suggestion in suggestionsOfType)
                {
                    suggestion.product_type_id = currentProductTypeId;
                    suggestion.product_type_name = currentProductTypeName;

                    await EnrichSuggestionWithPreviewAsync(
                        suggestion,
                        currentProductTypeId,
                        currentProductTypeName,
                        ct);
                }

                finalSuggestions.AddRange(suggestionsOfType);
            }

            return finalSuggestions
                .Where(x => x.suggest_order != null)
                .Where(x => x.suggest_order.Count >= 2)
                .Where(x => x.suggest_process != null)
                .Where(x => x.suggest_process.Count > 0)
                .OrderByDescending(x =>
                    string.Equals(x.suggestion_type, "GROUP_WITH_AUTO_SPLIT", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x =>
                    string.Equals(x.suggestion_type, "GROUP", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.product_type_id)
                .ThenBy(x => DepartmentOrder(x.department_code ?? ""))
                .ThenBy(x => string.Join(",", x.suggest_process))
                .ThenBy(x => x.common_delivery_deadline)
                .ToList();
        }

        private async Task EnrichSuggestionWithPreviewAsync(
    SuggestedGroupProductionDto suggestion,
    int productTypeId,
    string? productTypeName,
    CancellationToken ct)
        {
            if (suggestion.suggest_order == null || suggestion.suggest_order.Count < 2)
            {
                suggestion.note =
                    "Không đủ 2 order để tạo gợi ý ghép.";
                return;
            }

            if (suggestion.suggest_process == null || suggestion.suggest_process.Count == 0)
            {
                suggestion.note =
                    "Không có công đoạn hợp lệ để tạo gợi ý ghép.";
                return;
            }

            /*
             * SPLIT_ONLY không phải gợi ý group nhiều order.
             * Trường hợp này không cần gọi PreviewAsync.
             */
            if (string.Equals(suggestion.suggestion_type, "SPLIT_ONLY", StringComparison.OrdinalIgnoreCase))
            {
                suggestion.note =
                    "Đây là gợi ý tách riêng từng order, không phải gợi ý sản xuất ghép nhiều order.";
                return;
            }

            try
            {
                var preview = await PreviewAsync(new CreateGroupProductionRequest
                {
                    order_ids = suggestion.suggest_order,
                    process_codes = suggestion.suggest_process,
                    planned_start_date = null,
                    note = null
                }, ct);

                suggestion.suggested_planned_start_date = preview.suggested_planned_start_date;
                suggestion.common_delivery_deadline = preview.common_delivery_deadline;
                suggestion.estimated_finish_date = preview.estimated_finish_date;
                suggestion.estimated_total_days = preview.total_duration_days;
                suggestion.preview = preview;

                var processText = string.Join(",", suggestion.suggest_process);
                var orderText = string.Join(",", suggestion.suggest_order);

                var baseReason = string.IsNullOrWhiteSpace(suggestion.reason)
                    ? $"Các order {orderText} có thể ghép công đoạn {processText}."
                    : suggestion.reason.Trim();

                suggestion.note =
                    $"{baseReason} " +
                    $"ProductType={productTypeId}" +
                    $"{(string.IsNullOrWhiteSpace(productTypeName) ? "" : $" - {productTypeName}")}. " +
                    $"Mốc giao chung lấy theo order có ngày giao sớm nhất: {preview.common_delivery_deadline:yyyy-MM-dd}. " +
                    $"Ngày bắt đầu gợi ý: {preview.suggested_planned_start_date:yyyy-MM-dd}. " +
                    $"Dự kiến hoàn tất: {preview.estimated_finish_date:yyyy-MM-dd}.";
            }
            catch (Exception ex)
            {
                suggestion.note =
                    $"Gợi ý được tạo nhưng chưa build được preview lịch. Lý do: {ex.Message}";
            }
        }

        private static List<int> ParseOrderIdsCsv(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<int>();

            return raw
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => int.TryParse(x, out _))
                .Select(int.Parse)
                .Where(x => x > 0)
                .Distinct()
                .ToList();
        }

        private async Task SyncSingleDept1TaskTimelineAsync(
    List<GroupOrderRow> rows,
    GroupProductionConfirmPreviewResponse preview,
    CancellationToken ct)
        {
            var singleProdIds = rows
                .Select(x => x.SingleProd.prod_id)
                .Distinct()
                .ToList();

            var tasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id.HasValue && singleProdIds.Contains(x.prod_id.Value))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            var dept1Codes = Dept1Codes
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dept1Tasks = tasks
                .Where(x => dept1Codes.Contains(GroupProductionHelper.Norm(x.process?.process_code)))
                .ToList();

            if (dept1Tasks.Count == 0)
                return;

            foreach (var prodGroup in dept1Tasks.GroupBy(x => x.prod_id!.Value))
            {
                var ordered = prodGroup
                    .OrderBy(x => x.seq_num ?? int.MaxValue)
                    .ThenBy(x => x.task_id)
                    .ToList();

                var start = preview.dept1_private_stage.planned_start_date;
                var end = preview.dept1_private_stage.planned_end_date;

                var taskCount = Math.Max(ordered.Count, 1);
                var totalMinutes = Math.Max(1, (int)(end - start).TotalMinutes);
                var minutesPerTask = Math.Max(1, totalMinutes / taskCount);

                for (var i = 0; i < ordered.Count; i++)
                {
                    ordered[i].planned_start_time = start.AddMinutes(minutesPerTask * i);
                    ordered[i].planned_end_time = i == ordered.Count - 1
                        ? end
                        : start.AddMinutes(minutesPerTask * (i + 1));
                }
            }
        }

        private List<SuggestedGroupProductionDto> BuildSuggestionPreviewFromSelectedCodes(
    List<GroupOrderRow> rows,
    List<string> selectedCodes)
        {
            var normalizedSelectedCodes = selectedCodes
                .Select(NormProcessCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (normalizedSelectedCodes.Any(IsDept1))
            {
                var invalid = normalizedSelectedCodes
                    .Where(IsDept1)
                    .ToList();

                throw new InvalidOperationException(
                    $"Không được ghép/tách các công đoạn Dept1: {string.Join(",", invalid)}");
            }

            var plan = BuildDepartmentProductionPlan(
                rows,
                normalizedSelectedCodes,
                out var warnings);

            var groupSegments = plan
                .Where(x => x.IsGroup)
                .ToList();

            var splitSegments = plan
                .Where(x =>
                    !x.IsGroup &&
                    string.Equals(x.DepartmentCode, "DEPT_3", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var result = new List<SuggestedGroupProductionDto>();

            foreach (var group in groupSegments)
            {
                var groupOrderIds = group.Members
                    .Select(x => x.Order.order_id)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                var autoSplits = splitSegments
                    .Where(x =>
                        x.Members.Count == 1 &&
                        groupOrderIds.Contains(x.Members[0].Order.order_id))
                    .Select(ToSplitSuggestionDto)
                    .ToList();

                result.Add(new SuggestedGroupProductionDto
                {
                    suggestion_type = autoSplits.Count > 0
                        ? "GROUP_WITH_AUTO_SPLIT"
                        : "GROUP",

                    suggest_order = groupOrderIds,

                    suggest_process = group.ProcessCodes
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),

                    department_code = group.DepartmentCode,
                    department_name = group.DepartmentName,
                    material_key = group.MaterialKey,

                    auto_split_productions = autoSplits,
                    warnings = warnings,

                    reason = autoSplits.Count > 0
                        ? $"Có thể tạo nhóm {string.Join(",", group.ProcessCodes)}. Hệ thống sẽ tự tách BE/DUT/DAN riêng theo từng order."
                        : $"Có thể tạo nhóm {string.Join(",", group.ProcessCodes)}."
                });
            }

            if (result.Count == 0 && splitSegments.Count > 0)
            {
                result.Add(new SuggestedGroupProductionDto
                {
                    suggestion_type = "SPLIT_ONLY",

                    suggest_order = splitSegments
                        .SelectMany(x => x.Members)
                        .Select(x => x.Order.order_id)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList(),

                    suggest_process = splitSegments
                        .SelectMany(x => x.ProcessCodes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList(),

                    department_code = "DEPT_3",
                    department_name = ResolveDepartmentName("DEPT_3"),
                    material_key = null,

                    auto_split_productions = splitSegments
                        .Select(ToSplitSuggestionDto)
                        .ToList(),

                    warnings = warnings,

                    reason = "BE/DUT/DAN không được nhóm nhiều order do các công đoạn gia công cuối tùy thuộc từng yêu cầu kĩ thuật từng loại sản phẩm."
                });
            }

            return result;
        }

        private List<SuggestedGroupProductionDto> BuildAutoDept2Suggestions(
    List<GroupOrderRow> rows)
        {
            var raw = new List<SuggestedGroupProductionDto>();

            var possibleDept2Codes = rows
                .SelectMany(x => x.RouteCodes)
                .Where(IsDept2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => GetGlobalRouteIndex(rows, x))
                .ToList();

            foreach (var processCode in possibleDept2Codes)
            {
                var membersWithProcess = rows
                    .Where(x => x.RouteCodes.Contains(
                        processCode,
                        StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (membersWithProcess.Count < 2)
                    continue;

                if (RequiresSameMaterialKey(processCode))
                {
                    var materialGroups = membersWithProcess
                        .GroupBy(x => ResolveMaterialGroupKey(processCode, x))
                        .Where(g => g.Count() >= 2)
                        .ToList();

                    foreach (var mg in materialGroups)
                    {
                        raw.Add(new SuggestedGroupProductionDto
                        {
                            suggestion_type = "GROUP",

                            suggest_order = mg
                                .Select(x => x.Order.order_id)
                                .Distinct()
                                .OrderBy(x => x)
                                .ToList(),

                            suggest_process = new List<string> { processCode },

                            department_code = ResolveDepartmentCode(processCode),
                            department_name = ResolveDepartmentName(ResolveDepartmentCode(processCode)),

                            material_key = mg.Key,

                            reason = $"Các order cùng công đoạn {processCode} và cùng điều kiện vật tư."
                        });
                    }
                }
                else
                {
                    raw.Add(new SuggestedGroupProductionDto
                    {
                        suggestion_type = "GROUP",

                        suggest_order = membersWithProcess
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

            var merged = MergeDept2Suggestions(raw);

            foreach (var item in merged)
            {
                var memberRows = rows
                    .Where(x => item.suggest_order.Contains(x.Order.order_id))
                    .ToList();

                item.auto_split_productions = BuildAutoSplitSuggestionsForDept2(
                    memberRows,
                    item.suggest_process);

                if (item.auto_split_productions.Count > 0)
                {
                    item.suggestion_type = "GROUP_WITH_AUTO_SPLIT";
                    item.reason =
                        $"{item.reason} Nếu tạo nhóm này, hệ thống sẽ tự tách BE/DUT/DAN riêng từng order.";
                }
            }

            return merged
                .Where(x => x.suggest_order.Count >= 2)
                .ToList();
        }

        private static List<SuggestedGroupProductionDto> MergeDept2Suggestions(
    List<SuggestedGroupProductionDto> suggestions)
        {
            var result = new List<SuggestedGroupProductionDto>();

            foreach (var item in suggestions
                .OrderBy(x => DepartmentOrder(x.department_code ?? ""))
                .ThenBy(x => x.suggest_process.Count == 0
                    ? 999
                    : FullRouteIndex(x.suggest_process[0])))
            {
                var last = result.LastOrDefault();

                if (last != null &&
                    string.Equals(last.department_code, item.department_code, StringComparison.OrdinalIgnoreCase) &&
                    SameOrderIds(last.suggest_order, item.suggest_order))
                {
                    last.suggest_process = last.suggest_process
                        .Concat(item.suggest_process)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(FullRouteIndex)
                        .ToList();

                    last.material_key = CombineMaterialKeys(
                        last.material_key,
                        item.material_key);

                    last.reason =
                        $"Các order có thể sản xuất chung các công đoạn {string.Join(",", last.suggest_process)}.";

                    continue;
                }

                result.Add(item);
            }

            return result;
        }

        private static bool SameOrderIds(
            List<int> a,
            List<int> b)
        {
            return a
                .Distinct()
                .OrderBy(x => x)
                .SequenceEqual(
                    b.Distinct().OrderBy(x => x));
        }

        private static string? CombineMaterialKeys(
            string? a,
            string? b)
        {
            var keys = new List<string>();

            if (!string.IsNullOrWhiteSpace(a))
                keys.AddRange(a.Split(" | ", StringSplitOptions.RemoveEmptyEntries));

            if (!string.IsNullOrWhiteSpace(b))
                keys.AddRange(b.Split(" | ", StringSplitOptions.RemoveEmptyEntries));

            keys = keys
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return keys.Count == 0
                ? null
                : string.Join(" | ", keys);
        }

        private List<SuggestedSplitProductionDto> BuildAutoSplitSuggestionsForDept2(
    List<GroupOrderRow> rows,
    List<string> selectedProcessCodes)
        {
            var selectedDept2Codes = selectedProcessCodes
                .Select(NormProcessCode)
                .Where(IsDept2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedDept2Codes.Count == 0)
                return new List<SuggestedSplitProductionDto>();

            var result = new List<SuggestedSplitProductionDto>();

            foreach (var row in rows.OrderBy(x => x.Order.order_id))
            {
                var lastSelectedDept2Index = row.RouteCodes
                    .Select((code, index) => new
                    {
                        code = NormProcessCode(code),
                        index
                    })
                    .Where(x => selectedDept2Codes.Contains(
                        x.code,
                        StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.index)
                    .DefaultIfEmpty(-1)
                    .Max();

                if (lastSelectedDept2Index < 0)
                    continue;

                var dept3Codes = row.RouteCodes
                    .Skip(lastSelectedDept2Index + 1)
                    .Where(IsDept3)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList();

                if (dept3Codes.Count == 0)
                    continue;

                result.Add(new SuggestedSplitProductionDto
                {
                    order_id = row.Order.order_id,
                    order_code = row.Order.code,
                    single_prod_id = row.SingleProd.prod_id,
                    department_code = "DEPT_3",
                    department_name = ResolveDepartmentName("DEPT_3"),
                    process_codes = dept3Codes,
                    reason = $"Sau GROUP {string.Join(",", selectedDept2Codes)}, order {row.Order.order_id} sẽ được tách riêng {string.Join(",", dept3Codes)}."
                });
            }

            return result;
        }

        private SuggestedSplitProductionDto ToSplitSuggestionDto(
            ProductionPlanSegment segment)
        {
            var row = segment.Members.First();

            return new SuggestedSplitProductionDto
            {
                order_id = row.Order.order_id,
                order_code = row.Order.code,
                single_prod_id = row.SingleProd.prod_id,
                department_code = segment.DepartmentCode,
                department_name = segment.DepartmentName,
                process_codes = segment.ProcessCodes
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(FullRouteIndex)
                    .ToList(),
                reason = $"Tạo lệnh sản xuất riêng riêng cho order {row.Order.order_id}: {string.Join(",", segment.ProcessCodes)}."
            };
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
                throw new InvalidOperationException("Lệnh sản xuất này không phải lệnh sản xuất ghép.");

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
                can_start_message = canStart ? "Có thể bắt đầu lệnh sản xuất." : dep.message,

                product_type_id = prod.product_type_id,
                product_type_name = productTypeName,
                total_qty = prod.group_total_qty,
                process_codes = prod.group_process_codes,
                orders = orderRows,
                stages = stages,
                previous_stage_context = previousStageContext
            };
        }

        public async Task<GroupProductionConfirmPreviewResponse> PreviewAsync(
    CreateGroupProductionRequest req,
    CancellationToken ct = default)
        {
            if (req == null)
                throw new InvalidOperationException("Request body is required.");

            var orderIds = req.order_ids
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            if (orderIds.Count < 2)
                throw new InvalidOperationException("Cần chọn ít nhất 2 order để preview ghép.");

            var selectedCodes = req.process_codes
                .SelectMany(x => GroupProductionHelper.ParseCodes(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();

            if (selectedCodes.Count == 0)
                throw new InvalidOperationException("process_codes is required.");

            GroupProductionHelper.EnsureShareableCodes(selectedCodes);

            var rows = await LoadGroupOrderRowsAsync(orderIds, ct);

            if (rows.Count != orderIds.Count)
            {
                var found = rows.Select(x => x.Order.order_id).ToHashSet();
                var missing = orderIds.Where(x => !found.Contains(x)).ToList();

                throw new InvalidOperationException(
                    $"Không tìm thấy đủ order hợp lệ để preview. Missing: {string.Join(",", missing)}");
            }

            if (rows.Any(x => !x.Order.layout_confirmed || !x.Order.is_production_ready))
                throw new InvalidOperationException("Tất cả order phải xác nhận file thiết kế chuẩn và sẵn sàng sản xuất.");

            var invalidStatusOrders = rows
                .Where(x => !string.Equals(x.Order.status, "Scheduled", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Order.order_id)
                .ToList();

            if (invalidStatusOrders.Count > 0)
                throw new InvalidOperationException($"Order không ở trạng thái Đã lập lịch.");

            var productTypeIds = rows
                .Select(x => x.Item.product_type_id)
                .Distinct()
                .ToList();

            if (productTypeIds.Count != 1 || productTypeIds[0] == null)
                throw new InvalidOperationException("Các order phải cùng loại sản phẩm.");

            var plan = BuildDepartmentProductionPlan(
                rows,
                selectedCodes,
                out var warnings);

            if (plan.Count == 0)
                throw new InvalidOperationException("Không có công đoạn hợp lệ để preview.");

            var commonDeadline = ResolveCommonDeadline(rows);
            var suggestedStart = req.planned_start_date?.Date ?? ResolveSuggestedStart(commonDeadline);

            var dept1Start = suggestedStart;
            var dept1Stage = BuildStageDto(
                deptCode: "DEPT_1",
                deptName: "Dept 1 - RALO,CAT,IN riêng từng đơn",
                stageType: "SINGLE_PRIVATE",
                processCodes: Dept1Codes.ToList(),
                orderIds: orderIds,
                start: dept1Start,
                durationDays: Dept1Days,
                note: "Tất cả order phải hoàn tất Ralo, cắt, in trước khi bước ghép gia công bề mặt bắt đầu.");

            var groupStages = new List<GroupProductionScheduleStageDto>();
            var splitStages = new List<GroupProductionScheduleStageDto>();

            var dept2Start = dept1Stage.planned_end_date;
            var dept2End = dept2Start.AddDays(Dept2Days);
            var dept3Start = dept2End;

            foreach (var segment in plan)
            {
                if (segment.IsGroup)
                {
                    groupStages.Add(BuildStageDto(
                        deptCode: segment.DepartmentCode,
                        deptName: segment.DepartmentName,
                        stageType: "GROUP",
                        processCodes: segment.ProcessCodes,
                        orderIds: segment.Members.Select(x => x.Order.order_id).Distinct().ToList(),
                        start: dept2Start,
                        durationDays: Dept2Days,
                        note: $"Gợi ý ghép vì cùng loại sản phẩm, cùng nhóm vật liệu và cùng mốc giao chung {commonDeadline:yyyy-MM-dd}."));
                }
                else if (segment.DepartmentCode == "DEPT_3")
                {
                    splitStages.Add(BuildStageDto(
                        deptCode: segment.DepartmentCode,
                        deptName: segment.DepartmentName,
                        stageType: "SPLIT",
                        processCodes: segment.ProcessCodes,
                        orderIds: segment.Members.Select(x => x.Order.order_id).Distinct().ToList(),
                        start: dept3Start,
                        durationDays: Dept3Days,
                        note: "Phòng ban 3 là công đoạn cuối theo từng lệnh sản xuất, tách riêng để không làm sai luồng sản xuất từng đơn."));
                }
            }

            var timeline = new List<GroupProductionScheduleStageDto>();
            timeline.Add(dept1Stage);
            timeline.AddRange(groupStages);
            timeline.AddRange(splitStages);

            var estimatedFinish = timeline.Count == 0
                ? suggestedStart.AddDays(MinProductionDays)
                : timeline.Max(x => x.planned_end_date);

            var daysLate = Math.Max(0, (estimatedFinish.Date - commonDeadline.Date).Days);

            var notes = new List<string>
    {
        $"Mốc giao chung lấy theo đơn có ngày giao sớm nhất, nhưng không sớm hơn {MinProductionDays} ngày từ hiện tại.",
        $"Phòng ban 1 tối đa xong sau {Dept1Days} ngày.",
        $"Phòng ban 2 công đoạn ghép tối đa xong sau {Dept2Days} ngày.",
        $"Phòng ban 3 công đoạn cuối từng đơn tối đa xong sau {Dept3Days} ngày.",
        $"Tổng thời gian tối thiểu: {MinProductionDays} ngày."
    };

            notes.AddRange(warnings.Select(x => $"{x.process_code}: {x.reason}"));

            return new GroupProductionConfirmPreviewResponse
            {
                order_ids = orderIds,
                selected_process_codes = selectedCodes,
                common_delivery_deadline = commonDeadline,
                suggested_planned_start_date = suggestedStart,
                estimated_finish_date = estimatedFinish,
                total_duration_days = MinProductionDays,
                dept1_private_stage = dept1Stage,
                group_stages = groupStages,
                split_stages = splitStages,
                timeline = timeline.OrderBy(x => x.planned_start_date).ToList(),
                can_meet_common_deadline = daysLate == 0,
                days_late_if_any = daysLate,
                notes = notes
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

        private async Task LinkAndRemoveSingleTasksAsync(
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

            var singleProdIds = rows
                .Select(x => x.SingleProdId)
                .Distinct()
                .ToList();

            var singleTasks = await _db.tasks
                .Include(x => x.process)
                .Where(x => x.prod_id.HasValue && singleProdIds.Contains(x.prod_id.Value))
                .ToListAsync(ct);

            var taskIds = singleTasks.Select(x => x.task_id).ToList();

            var taskIdsWithLogs = await _db.task_logs
                .AsNoTracking()
                .Where(x => x.task_id.HasValue && taskIds.Contains(x.task_id.Value))
                .Select(x => x.task_id!.Value)
                .Distinct()
                .ToListAsync(ct);

            var taskIdsWithLogsSet = taskIdsWithLogs.ToHashSet();

            var tasksToRemove = new List<task>();

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

                    if (taskIdsWithLogsSet.Contains(singleTask.task_id) ||
                        string.Equals(singleTask.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(singleTask.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                        singleTask.start_time != null ||
                        singleTask.end_time != null)
                    {
                        throw new InvalidOperationException(
                            $"Không thể ghép công đoạn {code} của production {row.SingleProdId} vì task đã bắt đầu hoặc đã có log.");
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

                    tasksToRemove.Add(singleTask);
                }
            }

            _db.tasks.RemoveRange(tasksToRemove);
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
                var coating = ResolveCoatingMaterialCodeForGroup(row.Estimate);

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

        private static string? ResolveCoatingMaterialCodeForCompare(cost_estimate? est)
        {
            if (est == null)
                return null;

            if (!string.IsNullOrWhiteSpace(est.coating_material_code))
                return NormProcessCode(est.coating_material_code);

            var raw = NormProcessCode(est.coating_type);

            return raw switch
            {
                "KEO_NUOC" or "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" or "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" or "KEO_UV" or "PHU_UV" or "KEO_PHU_UV" => "KEO_PHU_UV",
                _ => string.IsNullOrWhiteSpace(raw) ? null : raw
            };
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
 *
 * Rule mới:
 * - Nếu user chọn trực tiếp BE/DUT/DAN => vẫn tạo SPLIT riêng từng order.
 * - Nếu user chọn bất kỳ công đoạn Dept2: PHU/CAN/CAN_MANG/BOI
 *   => tự động tách toàn bộ Dept3 phía sau: BE/DUT/DAN sang SPLIT riêng từng order.
 *
 * Mục tiêu:
 * - SINGLE gốc giữ Dept1: RALO/CAT/IN và shadow task GroupedWaiting của Dept2.
 * - GROUP giữ Dept2 được chọn: PHU/CAN/BOI...
 * - SPLIT giữ Dept3: BE/DUT/DAN riêng từng order.
 */
            var selectedDept2Codes = normalizedSelectedCodes
                .Where(IsDept2)
                .ToList();

            var selectedDept3Codes = normalizedSelectedCodes
                .Where(IsPrivateOrderProcess)
                .ToList();

            foreach (var row in rows.OrderBy(x => x.Order.order_id))
            {
                var privateCodesForOrder = new List<string>();

                /*
                 * Case A:
                 * User chọn trực tiếp BE/DUT/DAN.
                 */
                privateCodesForOrder.AddRange(
                    selectedDept3Codes
                        .Where(code => row.RouteCodes.Contains(code, StringComparer.OrdinalIgnoreCase)));

                /*
                 * Case B:
                 * User chọn PHU/CAN/BOI.
                 * Khi đã ghép/tách Dept2 thì Dept3 phía sau không được nằm chung production
                 * với RALO/CAT/IN nữa, nên tự động tách BE/DUT/DAN.
                 */
                if (selectedDept2Codes.Count > 0)
                {
                    var lastSelectedDept2Index = row.RouteCodes
                        .Select((code, index) => new
                        {
                            code = NormProcessCode(code),
                            index
                        })
                        .Where(x => selectedDept2Codes.Contains(
                            x.code,
                            StringComparer.OrdinalIgnoreCase))
                        .Select(x => x.index)
                        .DefaultIfEmpty(-1)
                        .Max();

                    if (lastSelectedDept2Index >= 0)
                    {
                        var dept3AfterSelectedDept2 = row.RouteCodes
                            .Skip(lastSelectedDept2Index + 1)
                            .Where(IsDept3)
                            .OrderBy(FullRouteIndex)
                            .ToList();

                        privateCodesForOrder.AddRange(dept3AfterSelectedDept2);
                    }
                }

                privateCodesForOrder = privateCodesForOrder
                    .Distinct(StringComparer.OrdinalIgnoreCase)
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
                    MaterialKey = $"ORDER:{row.Order.order_id}:DEPT_3"
                });
            }

            return MergeAdjacentSegments(result);
        }

        private static DateTime DateOnlyStart(DateTime value)
        {
            return value.Date;
        }

        private static DateTime MaxDate(DateTime a, DateTime b)
        {
            return a >= b ? a : b;
        }

        private static DateTime MinDate(IEnumerable<DateTime?> values)
        {
            var dates = values
                .Where(x => x.HasValue)
                .Select(x => x!.Value.Date)
                .ToList();

            if (dates.Count == 0)
                throw new InvalidOperationException("Tất cả order phải có delivery_date để ghép.");

            return dates.Min();
        }

        private static DateTime ResolveCommonDeadline(List<GroupOrderRow> rows)
        {
            var earliestDelivery = MinDate(rows.Select(x => x.Order.delivery_date));
            var minDeadline = AppTime.NowVnUnspecified().Date.AddDays(MinProductionDays);

            /*
             * Nếu đơn giao quá gấp, vẫn giữ rule tối thiểu 7 ngày.
             */
            return MaxDate(earliestDelivery, minDeadline);
        }

        private static DateTime ResolveSuggestedStart(DateTime commonDeadline)
        {
            return commonDeadline.Date.AddDays(-MinProductionDays);
        }

        private static GroupProductionScheduleStageDto BuildStageDto(
            string deptCode,
            string deptName,
            string stageType,
            List<string> processCodes,
            List<int> orderIds,
            DateTime start,
            int durationDays,
            string note)
        {
            return new GroupProductionScheduleStageDto
            {
                dept_code = deptCode,
                dept_name = deptName,
                stage_type = stageType,
                process_codes = processCodes,
                order_ids = orderIds,
                planned_start_date = start,
                planned_end_date = start.AddDays(durationDays),
                duration_days = durationDays,
                note = note
            };
        }

        private static DateTime ResolveStageStart(
            GroupProductionConfirmPreviewResponse preview,
            ProductionPlanSegment segment)
        {
            if (segment.IsGroup)
            {
                return preview.dept1_private_stage.planned_end_date;
            }

            if (segment.DepartmentCode == "DEPT_3")
            {
                var lastGroupEnd = preview.group_stages.Count == 0
                    ? preview.dept1_private_stage.planned_end_date
                    : preview.group_stages.Max(x => x.planned_end_date);

                return lastGroupEnd;
            }

            return preview.suggested_planned_start_date;
        }

        private static DateTime ResolveStageEnd(
            GroupProductionConfirmPreviewResponse preview,
            ProductionPlanSegment segment)
        {
            var start = ResolveStageStart(preview, segment);

            if (segment.IsGroup)
                return start.AddDays(Dept2Days);

            if (segment.DepartmentCode == "DEPT_3")
                return start.AddDays(Dept3Days);

            return start.AddDays(1);
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

        private static string? NormalizeProductionMethod(string? method)
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

        private static string ResolveSegmentProductionMethodOrThrow(ProductionPlanSegment segment)
        {
            if (segment == null)
                throw new InvalidOperationException("segment is required.");

            if (segment.Members == null || segment.Members.Count == 0)
                throw new InvalidOperationException("Segment không có order member.");

            var methods = segment.Members
                .Select(x => NormalizeProductionMethod(x.SingleProd.prod_method))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (methods.Count == 0)
            {
                var orderIds = segment.Members
                    .Select(x => x.Order.order_id)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                throw new InvalidOperationException(
                    $"Không xác định được prod_method SUB/NVL/BOTH từ production SINGLE gốc. Orders: {string.Join(",", orderIds)}");
            }

            if (methods.Count > 1)
            {
                var detail = segment.Members
                    .Select(x => $"order_id={x.Order.order_id}, single_prod_id={x.SingleProd.prod_id}, prod_method={x.SingleProd.prod_method}")
                    .ToList();

                throw new InvalidOperationException(
                    "Không thể tạo production GROUP/SPLIT từ các order có prod_method khác nhau. " +
                    "prod_method chỉ được là SUB/NVL/BOTH và phải thống nhất trong cùng segment. " +
                    $"Chi tiết: {string.Join(" | ", detail)}");
            }

            return methods[0]!;
        }

        private static string? ResolveCoatingMaterialCodeForGroup(cost_estimate? est)
        {
            if (est == null)
                return null;

            if (!string.IsNullOrWhiteSpace(est.coating_material_code))
                return NormProcessCode(est.coating_material_code);

            var raw = NormProcessCode(est.coating_type);

            return raw switch
            {
                "KEO_NUOC" or "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" or "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" or "KEO_UV" or "PHU_UV" or "KEO_PHU_UV" => "KEO_PHU_UV",
                _ => string.IsNullOrWhiteSpace(raw) ? null : raw
            };
        }
    }
}
