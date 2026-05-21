using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Socket;
using AMMS.Shared.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AMMS.Application.Services
{
    public class TaskScanService : ITaskScanService
    {
        private readonly NotificationService _noti;
        private readonly ITaskQrTokenService _tokenSvc;
        private readonly ITaskRepository _taskRepo;
        private readonly ITaskLogRepository _logRepo;
        private readonly IProductionRepository _prodRepo;
        private readonly IMachineRepository _machineRepo;
        private readonly IHubContext<RealtimeHub> _hub;
        private readonly IRequestRepository _orderRequestRepo;
        private readonly IOrderRepository _orderRepo;
        private readonly AppDbContext _db;

        public TaskScanService(
            NotificationService noti,
            AppDbContext db,
            ITaskQrTokenService tokenSvc,
            ITaskRepository taskRepo,
            ITaskLogRepository logRepo,
            IProductionRepository productionRepo,
            IMachineRepository machineRepo,
            IHubContext<RealtimeHub> hub,
            IRequestRepository orderRequestRepo,
            IOrderRepository orderRepo)
        {
            _noti = noti;
            _tokenSvc = tokenSvc;
            _taskRepo = taskRepo;
            _logRepo = logRepo;
            _prodRepo = productionRepo;
            _machineRepo = machineRepo;
            _hub = hub;
            _orderRequestRepo = orderRequestRepo;
            _orderRepo = orderRepo;
            _db = db;
        }

        public async Task<ScanTaskResult> ScanFinishAsync(
    ScanTaskRequest req,
    int? scannedByUserId,
    CancellationToken ct = default)
        {
            if (!_tokenSvc.TryValidate(req.token, out TaskQrTokenPayloadDto payload, out var reason))
                throw new ArgumentException(reason);

            var taskId = payload.task_id;
            var qtyGood = payload.qty_good;

            var isGroupTask = await IsGroupTaskAsync(taskId, ct);

            var manualMode =
                payload.use_manual_input ||
                await ShouldUseManualReportInputAsync(taskId, req, ct);

            List<TaskMaterialUsageLogItemDto> materialUsageSnapshot;

            if (manualMode)
            {
                var manualMaterials = NormalizeMaterialUsageInputs(payload.materials);
                ValidateManualMaterialUsageInput(manualMaterials);

                materialUsageSnapshot = await BuildManualMaterialUsageSnapshotAsync(
                    manualMaterials,
                    ct);
            }
            else
            {
                var rawQrMaterials = NormalizeMaterialUsageInputs(payload.materials);

                var expectedMaterials = await GetConsumableMaterialsForTaskAsync(taskId, ct);

                var qrMaterials = BuildReportMaterialUsageInputs(
                    expectedMaterials,
                    rawQrMaterials);

                ValidateMaterialUsageInput(expectedMaterials, qrMaterials);

                materialUsageSnapshot = BuildMaterialUsageSnapshot(
                    expectedMaterials,
                    qrMaterials);
            }

            var materialUsageJson = materialUsageSnapshot.Count == 0
                ? null
                : JsonSerializer.Serialize(materialUsageSnapshot, _jsonOptions);

            var normalizedReferenceInputs = NormalizeReferenceInputs(payload.reference_inputs);

            var referenceInputJson = manualMode && normalizedReferenceInputs.Count > 0
                ? JsonSerializer.Serialize(normalizedReferenceInputs, _jsonOptions)
                : null;

            if (isGroupTask)
            {
                var groupInfo = await _db.tasks
                    .AsNoTracking()
                    .Include(x => x.prod)
                    .Include(x => x.process)
                    .FirstOrDefaultAsync(x => x.task_id == taskId, ct)
                    ?? throw new InvalidOperationException("Task not found.");

                var maxAllowed = groupInfo.prod?.group_total_qty ?? 0;

                if (qtyGood <= 0)
                    throw new ArgumentOutOfRangeException(nameof(req.token), "qty_good phải lớn hơn 0.");

                if (maxAllowed > 0 && qtyGood > maxAllowed)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(req.token),
                        $"qty_good={qtyGood} vượt tổng số lượng production ghép ({maxAllowed}).");
                }
            }
            else
            {
                var policy = await _taskRepo.GetQtyPolicyAsync(taskId, ct);
                if (policy == null)
                    throw new InvalidOperationException("Không xác định được policy số lượng cho task.");

                if (qtyGood < policy.min_allowed || qtyGood > policy.max_allowed)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(req.token),
                        $"qty_good={qtyGood} không hợp lệ. " +
                        $"Công đoạn [{policy.process_code} - {policy.process_name}] " +
                        $"chỉ cho phép báo cáo trong khoảng {policy.min_allowed}..{policy.max_allowed} {policy.qty_unit}.");
                }
            }

            var result = await _taskRepo.ExecuteInTransactionAsync(async innerCt =>
            {
                var t = await _taskRepo.GetByIdAsync(taskId) ?? throw new Exception("Task not found");

                if (!t.prod_id.HasValue || !t.seq_num.HasValue)
                    throw new Exception("Task missing prod_id/seq_num");

                var dep = await ProductionDependencyValidator.CheckTaskCanStartAsync(
                    _db,
                    t.task_id,
                    innerCt);

                if (!dep.can_start)
                {
                    throw new InvalidOperationException(
                        "Không thể finish task vì công đoạn trước đó chưa hoàn thành. " +
                        dep.message);
                }

                var flowTasks = await _taskRepo.GetTasksByProductionWithProcessAsync(
                    t.prod_id.Value,
                    innerCt);

                var currentFlow = flowTasks.FirstOrDefault(x => x.task_id == t.task_id)
                    ?? throw new Exception("Task flow info not found");

                var currentCode = ProductionFlowHelper.Norm(currentFlow.process?.process_code);

                var hasRalo = flowTasks.Any(x =>
                    ProductionFlowHelper.IsRalo(x.process?.process_code));

                var raloFinished = !hasRalo || flowTasks.Any(x =>
                    ProductionFlowHelper.IsRalo(x.process?.process_code) &&
                    string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase));

                if (!hasRalo)
                {
                    var prev = await _taskRepo.GetPrevTaskAsync(t.prod_id.Value, t.seq_num.Value);

                    if (prev != null &&
                        !string.Equals(prev.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception(
                            $"Previous step (task_id={prev.task_id}, seq={prev.seq_num}) is not Finished");
                    }
                }
                else
                {
                    if (ProductionFlowHelper.NeedsRaloGate(currentCode) && !raloFinished)
                        throw new Exception("RALO must be Finished before this task can be scanned");
                }

                if (!string.Equals(t.status, "Ready", StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Task status '{t.status}' is not scannable. Only Ready can be finished.");

                var now = AppTime.NowVnUnspecified();

                if (!string.IsNullOrWhiteSpace(t.machine))
                    await _machineRepo.ReleaseAsync(t.machine!, release: 1);

                t.status = "Finished";
                t.start_time ??= now;
                t.end_time = now;

                var processCodeForOutput = currentFlow.process?.process_code;
                var processNameForOutput = currentFlow.process?.process_name;

                var normalizedOutputs = NormalizeOutputs(
                    payload.outputs,
                    processCodeForOutput,
                    processNameForOutput,
                    "sp",
                    qtyGood);

                var outputJson = manualMode
                    ? JsonSerializer.Serialize(normalizedOutputs, _jsonOptions)
                    : null;

                var finishLog = new task_log
                {
                    task_id = t.task_id,
                    scanned_code = req.token,
                    action_type = "Finished",
                    qty_good = qtyGood,
                    log_time = now,
                    scanned_by_user_id = scannedByUserId,

                    material_usage_json = materialUsageJson,

                    reason = NormalizeNullableText(payload.reason, 1000),
                    report_image_url = NormalizeNullableText(payload.report_image_url, 8000),

                    reference_input_json = referenceInputJson,
                    output_json = outputJson
                };

                await _logRepo.AddAsync(finishLog);

                await ReturnLeftoverMaterialsFromEstimatedFlowAsync(
                    materialUsageSnapshot,
                    t,
                    scannedByUserId,
                    now,
                    innerCt);

                /*
                 * Save trước để task_log có log_id.
                 */
                await _taskRepo.SaveChangesAsync(innerCt);

                /*
                 * Bổ sung:
                 * Nếu FE gửi reference_inputs_json có quantity_left > 0,
                 * tạo dòng sub_product tạm:
                 * - is_active = false
                 * - is_imported = false
                 *
                 * Dòng này chưa tham gia flow chọn method sản xuất.
                 * Warehouse sẽ nhập kho bằng API /api/SubProducts/import-pending.
                 */
                await CreatePendingSubProductsFromReferenceLeftoverAsync(
                    normalizedReferenceInputs,
                    t,
                    finishLog,
                    now,
                    innerCt);

                await _db.SaveChangesAsync(innerCt);

                await MirrorGroupFinishToSingleTasksAsync(
                    t,
                    finishLog,
                    qtyGood,
                    normalizedOutputs,
                    scannedByUserId,
                    now,
                    innerCt);

                /*
                 * Đóng production hiện tại nếu tất cả task đã Finished.
                 *
                 * Với SINGLE:
                 * - production.status = Importing
                 * - order.status = Importing
                 * - order_request.process_status = Importing
                 *
                 * Với GROUP:
                 * - group production.status = Importing
                 * - ProductionRepository sẽ kiểm tra từng member trong prod_orders
                 *   để sync order/request tương ứng nếu single production của order đó cũng đã xong.
                 */
                if (t.prod_id.HasValue)
                {
                    var changedToImporting = await _prodRepo.TryCloseProductionIfCompletedAsync(
                        t.prod_id.Value,
                        now,
                        innerCt);

                    if (changedToImporting)
                    {
                        await NotifyImportingForProductionAsync(
                            t.prod_id.Value,
                            innerCt);
                    }
                }

                await _hub.Clients.All.SendAsync(
                    "update-ui",
                    new { message = "update UI" },
                    innerCt);

                return new ScanTaskResult
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    message = $"Finished & logged qty_good={qtyGood}. Dữ liệu báo cáo được đọc từ QR token."
                };
            }, ct);

            /*
             * Không gửi finishedProduction ở đây nữa.
             * NotifyImportingForProductionAsync đã xử lý cả SINGLE và GROUP bên trong transaction.
             * Nếu giữ block cũ ở đây sẽ bị gửi thông báo trùng.
             */
            return result;
        }

        private async Task CreatePendingSubProductsFromReferenceLeftoverAsync(
    List<TaskReferenceUsageInputDto> referenceInputs,
    task currentTask,
    task_log finishLog,
    DateTime now,
    CancellationToken ct)
        {
            if (referenceInputs == null || referenceInputs.Count == 0)
                return;

            if (!currentTask.prod_id.HasValue)
                return;

            var leftovers = referenceInputs
                .Where(x => !string.IsNullOrWhiteSpace(x.input_code))
                .Where(x => x.quantity_left > 0)
                .ToList();

            if (leftovers.Count == 0)
                return;

            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == currentTask.prod_id.Value, ct);

            if (prod == null)
                return;

            var sourceOrderId = prod.order_id;

            /*
             * GROUP production không có order_id trực tiếp.
             * Lấy order đầu tiên trong prod_orders để lấy size/product_type.
             * Logic group của bạn đang ghép theo cùng loại/kích thước nên cách này không phá flow.
             */
            if (!sourceOrderId.HasValue)
            {
                sourceOrderId = await _db.prod_orders
                    .AsNoTracking()
                    .Where(x =>
                        x.prod_id == prod.prod_id &&
                        x.status == "Active")
                    .OrderBy(x => x.order_id)
                    .Select(x => (int?)x.order_id)
                    .FirstOrDefaultAsync(ct);
            }

            var item = sourceOrderId.HasValue
                ? await _db.order_items
                    .AsNoTracking()
                    .Where(x => x.order_id == sourceOrderId.Value)
                    .OrderBy(x => x.item_id)
                    .Select(x => new
                    {
                        x.product_type_id,
                        width = EF.Property<int?>(x, "width_mm"),
                        length = EF.Property<int?>(x, "length_mm")
                    })
                    .FirstOrDefaultAsync(ct)
                : null;

            var productTypeId = prod.product_type_id ?? item?.product_type_id;

            if (!productTypeId.HasValue || productTypeId.Value <= 0)
                return;

            foreach (var input in leftovers)
            {
                var qty = ToSubProductQty(input.quantity_left);
                if (qty <= 0)
                    continue;

                var processCode = NormSubProductProcess(input.input_code);

                if (string.IsNullOrWhiteSpace(processCode))
                    continue;

                var pending = new sub_product
                {
                    product_type_id = productTypeId.Value,
                    width = item?.width,
                    length = item?.length,
                    product_process = processCode,
                    quantity = qty,

                    /*
                     * Quan trọng:
                     * Chưa nhập kho thật nên không active.
                     * Không ảnh hưởng flow chọn SUB/BOTH hiện tại.
                     */
                    is_active = false,
                    is_imported = false,

                    source_task_id = currentTask.task_id,
                    source_task_log_id = finishLog.log_id > 0 ? finishLog.log_id : null,
                    source_prod_id = prod.prod_id,
                    source_order_id = sourceOrderId,

                    description =
                        $"BTP dư từ task_id={currentTask.task_id}, prod_id={prod.prod_id}, " +
                        $"process={processCode}, qty_left={input.quantity_left}, tạo lúc {now:yyyy-MM-dd HH:mm:ss}.",

                    updated_at = now
                };

                await _db.sub_products.AddAsync(pending, ct);
            }
        }

        private static int ToSubProductQty(decimal quantityLeft)
        {
            if (quantityLeft <= 0)
                return 0;

            /*
             * sub_product.quantity hiện là int.
             * Dùng Ceiling để không làm mất số lượng dư khi FE gửi decimal.
             */
            return (int)Math.Ceiling(quantityLeft);
        }

        private static string NormSubProductProcess(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private async Task NotifyImportingForProductionAsync(
    int prodId,
    CancellationToken ct)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == prodId, ct);

            if (prod == null)
                return;

            if (!string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase))
                return;

            var orderIds = new List<int>();

            var isGroupProduction = string.Equals(
                prod.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

            if (isGroupProduction)
            {
                orderIds = await _db.prod_orders
                    .AsNoTracking()
                    .Where(x =>
                        x.prod_id == prod.prod_id &&
                        x.status == "Active")
                    .Select(x => x.order_id)
                    .Distinct()
                    .ToListAsync(ct);
            }
            else if (prod.order_id.HasValue)
            {
                orderIds.Add(prod.order_id.Value);
            }

            foreach (var orderId in orderIds.Distinct())
            {
                var ord = await _db.orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.order_id == orderId, ct);

                if (ord == null)
                    continue;

                /*
                 * ProductionRepository là nơi quyết định order nào thật sự được chuyển Importing.
                 * Nếu order vẫn chưa Importing thì không gửi thông báo, tránh báo sai.
                 *
                 * Ví dụ:
                 * - Group xong PHU,CAN,BOI,BE,DUT
                 * - Nhưng order A còn DAN riêng chưa xong
                 * => order A vẫn InProcessing
                 * => không gửi notify Importing cho order A.
                 */
                if (!string.Equals(ord.status, "Importing", StringComparison.OrdinalIgnoreCase))
                    continue;

                var requests = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => x.order_id == orderId)
                    .OrderByDescending(x => x.order_request_id)
                    .ToListAsync(ct);

                var latestRequest = requests.FirstOrDefault();

                await _hub.Clients.All.SendAsync(
                    "finishedProduction",
                    new
                    {
                        message = $"Đơn hàng {orderId} đã được sản xuất xong"
                    },
                    ct);

                await _hub.Clients.Group(RealtimeGroups.ByRole("general manager")).SendAsync(
                    "Importing",
                    new
                    {
                        message = $"Đơn hàng {orderId} đã được sản xuất xong, chờ nhập kho"
                    },
                    ct);

                if (latestRequest != null)
                {
                    await _noti.CreateNotfi(
                        4,
                        $"Đơn hàng {orderId} đã được sản xuất xong, chờ nhập kho",
                        null,
                        latestRequest.order_request_id,
                        "Importing");

                    await _noti.CreateNotfi(
                        18,
                        $"Đơn hàng {orderId} đã được sản xuất xong, chờ nhập kho",
                        null,
                        latestRequest.order_request_id,
                        "Importing");
                }
            }
        }

        private async Task ConsumeManualMaterialsOnFinishAsync(
    List<TaskMaterialUsageLogItemDto> materialUsageSnapshot,
    task t,
    int? scannedByUserId,
    DateTime now,
    CancellationToken ct)
        {
            foreach (var item in materialUsageSnapshot)
            {
                if (item.quantity_used <= 0)
                    continue;

                var mat = await _db.materials
                    .FirstOrDefaultAsync(x => x.material_id == item.material_id, ct);

                if (mat == null)
                    throw new InvalidOperationException($"Material not found. material_id={item.material_id}");

                var currentStock = mat.stock_qty ?? 0m;

                if (currentStock < item.quantity_used)
                {
                    throw new InvalidOperationException(
                        $"Không đủ tồn kho NVL {mat.code} - {mat.name}. " +
                        $"Tồn={currentStock}, cần xuất={item.quantity_used}");
                }

                mat.stock_qty = currentStock - item.quantity_used;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = item.material_id,
                    type = "OUT",
                    qty = item.quantity_used,
                    ref_doc = $"TASK-MANUAL-USE-{t.task_id}",
                    user_id = scannedByUserId,
                    move_date = now,
                    note = $"Manual consume material on task finish. task_id={t.task_id}, prod_id={t.prod_id}",
                }, ct);
            }
        }

        private async Task ReturnLeftoverMaterialsFromEstimatedFlowAsync(
    List<TaskMaterialUsageLogItemDto> materialUsageSnapshot,
    task t,
    int? scannedByUserId,
    DateTime now,
    CancellationToken ct)
        {
            foreach (var item in materialUsageSnapshot)
            {
                if (item.quantity_left <= 0 || !item.is_stock)
                    continue;

                var mat = await _db.materials
                    .FirstOrDefaultAsync(x => x.material_id == item.material_id, ct);

                if (mat == null)
                    throw new InvalidOperationException($"Material not found. material_id={item.material_id}");

                mat.stock_qty = (mat.stock_qty ?? 0m) + item.quantity_left;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = item.material_id,
                    type = "IN",
                    qty = item.quantity_left,
                    ref_doc = $"TASK-LEFTOVER-{t.task_id}",
                    user_id = scannedByUserId,
                    move_date = now,
                    note = $"Return leftover from task_id={t.task_id}, prod_id={t.prod_id}",
                }, ct);
            }
        }

        private async Task MirrorGroupFinishToSingleTasksAsync(
    task groupTask,
    task_log groupLog,
    int groupQtyGood,
    List<TaskOutputReportDto> groupOutputs,
    int? scannedByUserId,
    DateTime now,
    CancellationToken ct)
        {
            if (!groupTask.prod_id.HasValue)
                return;

            var groupProd = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == groupTask.prod_id.Value, ct);

            if (groupProd == null ||
                !string.Equals(groupProd.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var links = await _db.task_links
                .Where(x =>
                    x.group_task_id == groupTask.task_id &&
                    x.status != "Done")
                .OrderBy(x => x.id)
                .ToListAsync(ct);

            if (links.Count == 0)
                return;

            var allocations = AllocateGroupQtyByLinks(groupQtyGood, links);

            foreach (var link in links)
            {
                var qtyForOrder = allocations.TryGetValue(link.order_id, out var q)
                    ? q
                    : 0;

                var allocatedOutputs = AllocateOutputsForOrder(
                    groupOutputs,
                    qtyForOrder);

                var allocatedOutputJson = allocatedOutputs.Count == 0
                    ? null
                    : JsonSerializer.Serialize(allocatedOutputs, _jsonOptions);

                await _db.task_qtys.AddAsync(new task_qty
                {
                    task_log_id = groupLog.log_id == 0 ? null : groupLog.log_id,

                    group_task_id = groupTask.task_id,

                    // Task single đã bị xóa.
                    single_task_id = null,

                    single_prod_id = link.single_prod_id,
                    order_id = link.order_id,
                    process_code = link.process_code,

                    qty_good = qtyForOrder,
                    output_json = allocatedOutputJson,
                    created_at = now
                }, ct);

                link.status = "Done";
                link.done_at = now;
            }

            await _db.SaveChangesAsync(ct);

            var affectedSingleProdIds = links
                .Select(x => x.single_prod_id)
                .Distinct()
                .ToList();

            foreach (var singleProdId in affectedSingleProdIds)
            {
                await PromoteFirstAvailableSingleTaskAfterGroupedStageAsync(
                    singleProdId,
                    now,
                    ct);

                await _prodRepo.TryCloseProductionIfCompletedAsync(
                    singleProdId,
                    now,
                    ct);
            }
        }

        private static Dictionary<int, int> AllocateGroupQtyByLinks(
            int groupQtyGood,
            List<task_link> links)
        {
            var result = new Dictionary<int, int>();

            var totalPlan = links.Sum(x => x.qty_plan);

            if (totalPlan <= 0)
            {
                foreach (var link in links)
                    result[link.order_id] = 0;

                return result;
            }

            var remaining = groupQtyGood;

            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];

                int qty;

                if (i == links.Count - 1)
                {
                    qty = remaining;
                }
                else
                {
                    qty = (int)Math.Round(
                        groupQtyGood * (link.qty_plan / (decimal)totalPlan),
                        MidpointRounding.AwayFromZero);

                    if (qty < 0)
                        qty = 0;

                    if (qty > remaining)
                        qty = remaining;
                }

                result[link.order_id] = qty;
                remaining -= qty;
            }

            return result;
        }

        private async Task PromoteFirstAvailableSingleTaskAfterGroupedStageAsync(
    int singleProdId,
    DateTime now,
    CancellationToken ct)
        {
            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == singleProdId, ct);

            if (prod == null)
                return;

            if (!string.Equals(prod.status, "InProcessing", StringComparison.OrdinalIgnoreCase))
                return;

            var candidates = await _db.tasks
                .Include(x => x.process)
                .Where(x =>
                    x.prod_id == singleProdId &&
                    (
                        x.status == null ||
                        (
                            x.status.ToUpper() != "FINISHED" &&
                            x.status.ToUpper() != "READY"
                        )
                    ))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            foreach (var candidate in candidates)
            {
                var dep = await ProductionDependencyValidator.CheckTaskCanStartAsync(
                    _db,
                    candidate.task_id,
                    ct);

                if (!dep.can_start)
                    continue;

                candidate.status = "Ready";
                candidate.start_time ??= now;
                break;
            }

            await _db.SaveChangesAsync(ct);
        }

        private static List<TaskOutputReportDto> AllocateOutputsForOrder(
            List<TaskOutputReportDto> groupOutputs,
            int qtyForOrder)
        {
            if (groupOutputs == null || groupOutputs.Count == 0)
                return new List<TaskOutputReportDto>();

            var totalGood = groupOutputs.Sum(x => x.quantity_good);

            if (totalGood <= 0)
            {
                return groupOutputs.Select(x => new TaskOutputReportDto
                {
                    output_code = x.output_code,
                    output_name = x.output_name,
                    unit = x.unit,
                    quantity_good = 0,
                    quantity_bad = 0
                }).ToList();
            }

            return groupOutputs.Select(x =>
            {
                var ratio = x.quantity_good / totalGood;

                return new TaskOutputReportDto
                {
                    output_code = x.output_code,
                    output_name = x.output_name,
                    unit = x.unit,
                    quantity_good = Math.Round(qtyForOrder * ratio, 4),
                    quantity_bad = 0
                };
            }).ToList();
        }

        private static string? NormalizeNullableText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var text = value.Trim();

            if (maxLength > 0 && text.Length > maxLength)
                text = text[..maxLength];

            return text;
        }

        private static void ValidateMaterialUsageInput(
    List<TaskConsumableMaterialDto> expectedMaterials,
    List<TaskMaterialUsageInputDto> inputMaterials)
        {
            if (expectedMaterials.Any(x => !x.is_mapped))
            {
                var unmapped = string.Join(", ",
                    expectedMaterials
                        .Where(x => !x.is_mapped)
                        .Select(x => x.material_code));

                throw new InvalidOperationException(
                    $"Có NVL chưa map sang materials: {unmapped}. Không thể finish task.");
            }

            if (expectedMaterials.Count == 0)
            {
                if (inputMaterials != null && inputMaterials.Count > 0)
                    throw new InvalidOperationException("Task này không yêu cầu nhập NVL.");
                return;
            }

            if (inputMaterials == null || inputMaterials.Count == 0)
                throw new InvalidOperationException("Bắt buộc nhập danh sách NVL dư khi báo cáo công đoạn.");

            if (inputMaterials.Count != expectedMaterials.Count)
                throw new InvalidOperationException("Số lượng NVL nhập vào không khớp với số NVL ước tính.");

            var duplicated = inputMaterials
                .GroupBy(x => x.material_id)
                .Any(g => g.Count() > 1);

            if (duplicated)
                throw new InvalidOperationException("Danh sách NVL bị trùng material_id.");

            foreach (var expected in expectedMaterials)
            {
                if (!expected.material_id.HasValue || expected.material_id.Value <= 0)
                    throw new InvalidOperationException($"NVL {expected.material_code} chưa có material_id hợp lệ.");

                var input = inputMaterials.FirstOrDefault(x => x.material_id == expected.material_id.Value);
                if (input == null)
                    throw new InvalidOperationException($"Thiếu dữ liệu NVL material_id={expected.material_id.Value}.");

                if (input.quantity_used < 0)
                    throw new InvalidOperationException(
                        $"quantity_used của {expected.material_code} không được nhỏ hơn 0.");

                if (input.quantity_left < 0)
                    throw new InvalidOperationException(
                        $"quantity_left của {expected.material_code} không được nhỏ hơn 0.");

                if (input.quantity_used + input.quantity_left > expected.estimated_input_qty)
                    throw new InvalidOperationException(
                        $"NVL {expected.material_code} vượt số lượng input ước tính. " +
                        $"Used + Left = {input.quantity_used + input.quantity_left}, " +
                        $"Estimated = {expected.estimated_input_qty}");

                if (input.quantity_left > 0 && !input.is_stock)
                    throw new InvalidOperationException(
                        $"NVL {expected.material_code} có dư thì is_stock phải = true.");
            }
        }

        public async Task<int> SuggestQtyGoodAsync(int taskId, CancellationToken ct = default)
        {
            var policy = await _taskRepo.GetQtyPolicyAsync(taskId, ct);
            return policy?.suggested_qty ?? 1;
        }

        public async Task<TaskQrMaterialBundleDto> GetTaskQrMaterialBundleAsync(
    int taskId,
    CancellationToken ct = default)
        {
            var groupBundle = await TryBuildGroupTaskQrMaterialBundleAsync(taskId, ct);
            if (groupBundle != null)
                return groupBundle;

            var ctx = await GetTaskEstimateContextAsync(taskId, ct);
            if (ctx == null)
                return new TaskQrMaterialBundleDto();

            return new TaskQrMaterialBundleDto
            {
                consumable_materials = await BuildConsumableMaterialsAsync(ctx, ct),
                reference_inputs = await BuildReferenceInputsAsync(ctx, ct)
            };
        }

        private async Task<TaskQrMaterialBundleDto?> TryBuildGroupTaskQrMaterialBundleAsync(
    int groupTaskId,
    CancellationToken ct)
        {
            var groupTask = await _db.tasks
                .AsNoTracking()
                .Include(x => x.prod)
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == groupTaskId, ct);

            if (groupTask?.prod == null ||
                !string.Equals(groupTask.prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

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
            {
                return new TaskQrMaterialBundleDto
                {
                    consumable_materials = new List<TaskConsumableMaterialDto>(),
                    reference_inputs = new List<TaskReferenceInputDto>()
                };
            }

            var allMaterials = new List<TaskConsumableMaterialDto>();
            var allRefs = new List<TaskReferenceInputDto>();

            foreach (var link in links)
            {
                var singleCtx = await GetGroupLinkEstimateContextAsync(
                    groupTask,
                    link,
                    ct);

                if (singleCtx == null)
                    continue;

                var mats = await BuildConsumableMaterialsAsync(singleCtx, ct);
                var refs = await BuildReferenceInputsForGroupLinkAsync(
                    singleCtx,
                    link.order_id,
                    link.process_code ?? groupTask.process?.process_code,
                    ct);

                allMaterials.AddRange(mats);
                allRefs.AddRange(refs);
            }

            return new TaskQrMaterialBundleDto
            {
                consumable_materials = AggregateConsumableMaterials(allMaterials),
                reference_inputs = AggregateReferenceInputs(allRefs)
            };
        }

        private async Task<TaskEstimateContext?> GetGroupLinkEstimateContextAsync(
    task groupTask,
    task_link link,
    CancellationToken ct)
        {
            if (link.single_prod_id <= 0)
                return null;

            if (link.order_id <= 0)
                return null;

            var singleProd = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.prod_id == link.single_prod_id &&
                    x.order_id == link.order_id,
                    ct);

            if (singleProd == null)
                return null;

            var req = await _db.order_requests
                .AsNoTracking()
                .Where(x => x.order_id == link.order_id)
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

            if (est == null)
                return null;

            /*
             * Task single đã bị xóa nên không thể lấy theo single_task_id.
             * Tạo task context giả để BuildConsumableMaterialsAsync đọc process_code.
             */
            var fakeTask = new task
            {
                task_id = groupTask.task_id,
                prod_id = singleProd.prod_id,
                seq_num = groupTask.seq_num,
                process_id = groupTask.process_id,
                process = groupTask.process,
                input_mode = "MANUAL"
            };

            return new TaskEstimateContext
            {
                Task = fakeTask,
                Production = singleProd,
                Request = req,
                Estimate = est
            };
        }

        private async Task<List<TaskReferenceInputDto>> BuildReferenceInputsForGroupLinkAsync(
    TaskEstimateContext ctx,
    int orderId,
    string? currentProcessCode,
    CancellationToken ct)
        {
            var currentCode = ProductionFlowHelper.Norm(currentProcessCode);

            if (string.IsNullOrWhiteSpace(currentCode))
                return new List<TaskReferenceInputDto>();

            var routeCodes = await ResolveRouteProcessCodesForOrderAsync(orderId, ct);

            if (routeCodes.Count == 0)
                return new List<TaskReferenceInputDto>();

            var currentIndex = routeCodes.FindIndex(x =>
                string.Equals(
                    ProductionFlowHelper.Norm(x),
                    currentCode,
                    StringComparison.OrdinalIgnoreCase));

            if (currentIndex <= 0)
                return new List<TaskReferenceInputDto>();

            var previousCode = ProductionFlowHelper.Norm(routeCodes[currentIndex - 1]);

            var (unit, qty) = ResolveReferenceInputShape(
                currentProcessCode: currentCode,
                currentStageIndex: currentIndex,
                routeProcessCodes: routeCodes,
                ctx: ctx);

            return new List<TaskReferenceInputDto>
    {
        new TaskReferenceInputDto
        {
            input_code = previousCode,
            input_name = $"Bán thành phẩm từ công đoạn {previousCode}",
            unit = unit,
            estimated_qty = Math.Round(qty, 4)
        }
    };
        }

        private async Task<List<string?>> ResolveRouteProcessCodesForOrderAsync(
    int orderId,
    CancellationToken ct)
        {
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

            var fromOrderItem = ParseProcessRouteCsv(item?.production_process)
                .Cast<string?>()
                .ToList();

            if (fromOrderItem.Count > 0)
                return fromOrderItem;

            if (item?.product_type_id == null || item.product_type_id.Value <= 0)
                return new List<string?>();

            return await _db.product_type_processes
                .AsNoTracking()
                .Where(x =>
                    x.product_type_id == item.product_type_id.Value &&
                    (x.is_active ?? true))
                .OrderBy(x => x.seq_num)
                .Select(x => (string?)x.process_code)
                .ToListAsync(ct);
        }

        private static List<string> ParseProcessRouteCsv(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<string>();

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToUpperInvariant())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<TaskConsumableMaterialDto> AggregateConsumableMaterials(
    List<TaskConsumableMaterialDto> items)
        {
            if (items == null || items.Count == 0)
                return new List<TaskConsumableMaterialDto>();

            return items
                .GroupBy(x => new
                {
                    material_id = x.material_id ?? 0,
                    material_code = NormalizeMaterialCode(x.material_code),
                    unit = (x.unit ?? "").Trim().ToLowerInvariant()
                })
                .Select(g =>
                {
                    var first = g.First();

                    return new TaskConsumableMaterialDto
                    {
                        material_id = first.material_id,
                        material_code = first.material_code,
                        material_name = first.material_name,
                        unit = first.unit,
                        estimated_input_qty = Math.Round(g.Sum(x => x.estimated_input_qty), 4),
                        is_mapped = g.All(x => x.is_mapped)
                    };
                })
                .OrderBy(x => x.material_code)
                .ToList();
        }

        private static List<TaskReferenceInputDto> AggregateReferenceInputs(
    List<TaskReferenceInputDto> items)
        {
            if (items == null || items.Count == 0)
                return new List<TaskReferenceInputDto>();

            return items
                .GroupBy(x => new
                {
                    input_code = ProductionFlowHelper.Norm(x.input_code),
                    unit = (x.unit ?? "").Trim().ToLowerInvariant()
                })
                .Select(g =>
                {
                    var first = g.First();

                    return new TaskReferenceInputDto
                    {
                        input_code = first.input_code,
                        input_name = first.input_name,
                        unit = first.unit,
                        estimated_qty = Math.Round(g.Sum(x => x.estimated_qty), 4)
                    };
                })
                .OrderBy(x => x.input_code)
                .ToList();
        }

        private static string RemoveDiacritics(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();

            foreach (var ch in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string NormalizeMaterialCode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            var s = RemoveDiacritics(raw)
                .Trim()
                .ToUpperInvariant();

            s = s.Replace("Đ", "D");
            s = Regex.Replace(s, @"[^A-Z0-9]+", "_");
            s = Regex.Replace(s, @"_+", "_").Trim('_');

            return s switch
            {
                // Kẽm
                "KEM_THO" => "PLATE",
                "BAN_KEM_THO" => "PLATE",
                "BAN_KEM" => "PLATE",
                "BAN_KEM_IN" => "PLATE",
                "PLATE_INPUT" => "PLATE",

                // Mực
                "MUC" => "INK",
                "MUC_IN" => "INK",
                "MUC_TONG_HOP" => "INK",
                "INK_TYPES" => "INK",

                // Keo phủ
                "KEO_NUOC" => "KEO_PHU_NUOC",
                "KEO_PHU_NUOC" => "KEO_PHU_NUOC",
                "KEO_DAU" => "KEO_PHU_DAU",
                "KEO_PHU_DAU" => "KEO_PHU_DAU",
                "UV" => "KEO_PHU_UV",
                "KEO_UV" => "KEO_PHU_UV",
                "PHU_UV" => "KEO_PHU_UV",
                "KEO_PHU_UV" => "KEO_PHU_UV",

                // Màng
                "MANG_12_MIC" => "MANG_12MIC",

                // Keo bồi
                "MOUNTING_GLUE" => "KEO_BOI",
                "KEO_BOI" => "KEO_BOI",

                _ => s
            };
        }

        private async Task<List<TaskConsumableMaterialDto>> BuildConsumableMaterialsAsync(
    TaskEstimateContext ctx,
    CancellationToken ct = default)
        {
            var t = ctx.Task;
            var req = ctx.Request;
            var est = ctx.Estimate;

            var processCode = (t.process?.process_code ?? "").Trim().ToUpperInvariant();
            var result = new List<TaskConsumableMaterialDto>();
            var bothScale = await ResolveBothConsumableScaleAsync(ctx, ct);

            async Task AddMaterialAsync(
    decimal estimatedQty,
    string fallbackCode,
    string fallbackName,
    string unit,
    material? resolvedMaterial,
    bool ceilWhenBothRatio = false)
            {
                if (bothScale.ShouldScale)
                    estimatedQty = estimatedQty * bothScale.Ratio;

                if (estimatedQty <= 0)
                    return;

                if (bothScale.ShouldScale && ceilWhenBothRatio)
                    estimatedQty = Math.Ceiling(estimatedQty);

                var normalizedFallbackCode = NormalizeMaterialCode(
                    !string.IsNullOrWhiteSpace(fallbackCode) ? fallbackCode : fallbackName);

                result.Add(new TaskConsumableMaterialDto
                {
                    material_id = resolvedMaterial?.material_id,
                    material_code = resolvedMaterial?.code ?? normalizedFallbackCode,
                    material_name = resolvedMaterial?.name ?? fallbackName,
                    unit = resolvedMaterial?.unit ?? unit,
                    estimated_input_qty = Math.Round(estimatedQty, 4),
                    is_mapped = resolvedMaterial != null
                });
            }

            switch (processCode)
            {
                case "RALO":
                    {
                        var plateMat = await ResolveMaterialByCodesOrNamesAsync(
                            ct,
                            new[] { "PLATE", "PLATE_INPUT" },
                            new[] { "Kẽm thô", "Bản kẽm thô" });

                        await AddMaterialAsync(
                            estimatedQty: Math.Max(req.number_of_plates ?? 0, 1),
                            fallbackCode: "PLATE",
                            fallbackName: "Kẽm thô",
                            unit: "bản",
                            resolvedMaterial: plateMat);
                        break;
                    }

                case "CAT":
                    {
                        var paperMat = await ResolvePaperMaterialAsync(est, ct);

                        await AddMaterialAsync(
                            estimatedQty: est.sheets_total > 0 ? est.sheets_total : est.sheets_required,
                            fallbackCode: est.paper_code ?? "PAPER",
                            fallbackName: est.paper_name ?? "Giấy thô",
                            unit: "tờ",
                            resolvedMaterial: paperMat,
                            ceilWhenBothRatio: true);
                        break;
                    }

                case "IN":
                    {
                        // 1. Giấy đưa vào máy in
                        var paperMat = await ResolvePaperMaterialAsync(est, ct);

                        await AddMaterialAsync(
                            estimatedQty: est.sheets_total > 0 ? est.sheets_total : est.sheets_required,
                            fallbackCode: est.paper_code ?? "PAPER",
                            fallbackName: est.paper_name ?? "Giấy in",
                            unit: "tờ",
                            resolvedMaterial: paperMat,
                            ceilWhenBothRatio: true);

                        // 2. Mực in
                        var inkMat = await ResolveMaterialByCodesOrNamesAsync(
                            ct,
                            new[] { "INK" },
                            new[] { "Mực tổng hợp", "Mực in" });

                        await AddMaterialAsync(
                            estimatedQty: est.ink_weight_kg,
                            fallbackCode: "INK",
                            fallbackName: "Mực tổng hợp",
                            unit: "kg",
                            resolvedMaterial: inkMat);

                        break;
                    }

                case "PHU":
                    {
                        var coatingMat = await ResolveCoatingMaterialAsync(est, ct);

                        await AddMaterialAsync(
                            estimatedQty: est.coating_glue_weight_kg,
                            fallbackCode: NormalizeMaterialCode(est.coating_type),
                            fallbackName: ProductionFlowHelper.ResolveCoatingDisplayName(est.coating_type),
                            unit: "kg",
                            resolvedMaterial: coatingMat);
                        break;
                    }

                case "CAN":
                case "CAN_MANG":
                    {
                        var filmMat = await ResolveLaminationMaterialAsync(est, ct);

                        var fallbackCode = !string.IsNullOrWhiteSpace(est.lamination_material_code)
                            ? est.lamination_material_code
                            : "LAMINATION_NOT_SELECTED";

                        var fallbackName = !string.IsNullOrWhiteSpace(est.lamination_material_name)
                            ? est.lamination_material_name
                            : "Chưa chọn loại màng cán";

                        await AddMaterialAsync(
                            estimatedQty: est.lamination_weight_kg,
                            fallbackCode: fallbackCode,
                            fallbackName: fallbackName,
                            unit: filmMat?.unit ?? "kg",
                            resolvedMaterial: filmMat);

                        break;
                    }

                case "BOI":
                    {
                        var waveMat = await ResolveWaveMaterialAsync(est, ct);
                        var resolvedWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                            est.wave_alternative,
                            est.wave_type);

                        await AddMaterialAsync(
                            estimatedQty: est.wave_sheets_used ?? est.wave_sheets_required ?? 0,
                            fallbackCode: waveMat?.code ?? resolvedWaveType ?? "WAVE",
                            fallbackName: waveMat?.name ?? (string.IsNullOrWhiteSpace(resolvedWaveType) ? "Sóng carton" : $"Sóng {resolvedWaveType}"),
                            unit: waveMat?.unit ?? "tờ",
                            resolvedMaterial: waveMat);

                        var glueMat = await ResolveMaterialByCodesOrNamesAsync(
                            ct,
                            new[] { "KEO_BOI", "MOUNTING_GLUE" },
                            new[] { "Keo bồi" });

                        await AddMaterialAsync(
                            estimatedQty: est.mounting_glue_weight_kg,
                            fallbackCode: "KEO_BOI",
                            fallbackName: "Keo bồi",
                            unit: "kg",
                            resolvedMaterial: glueMat);
                        break;
                    }

                // BE / DUT / DAN không phải NVL kho trực tiếp
                case "BE":
                case "DUT":
                case "DAN":
                default:
                    break;
            }

            return result;
        }

        private async Task<List<TaskReferenceInputDto>> BuildReferenceInputsAsync(
    TaskEstimateContext ctx,
    CancellationToken ct = default)
        {
            var t = ctx.Task;
            var currentCode = ProductionFlowHelper.Norm(t.process?.process_code);

            var previousCtx = await GetPreviousProcessContextAsync(t, ct);
            if (previousCtx == null)
                return new List<TaskReferenceInputDto>();

            var currentStageIndex = previousCtx.previous_stage_index + 1;

            var (unit, qty) = ResolveReferenceInputShape(
                currentProcessCode: currentCode,
                currentStageIndex: currentStageIndex,
                routeProcessCodes: previousCtx.route_process_codes,
                ctx: ctx);

            var result = new List<TaskReferenceInputDto>();

            switch (currentCode)
            {
                case "IN":
                case "PHU":
                case "CAN":
                case "CAN_MANG":
                case "BOI":
                case "BE":
                case "DUT":
                case "DAN":
                    result.Add(new TaskReferenceInputDto
                    {
                        input_code = previousCtx.previous_process_code,
                        input_name = $"Bán thành phẩm từ công đoạn {previousCtx.previous_process_code}",
                        unit = unit,
                        estimated_qty = Math.Round(qty, 4)
                    });
                    break;
            }

            return result;
        }

        private async Task<PreviousProcessContext?> GetPreviousProcessContextAsync(
    task currentTask,
    CancellationToken ct = default)
        {
            if (!currentTask.prod_id.HasValue)
                return null;

            var flow = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == currentTask.prod_id.Value)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .Select(x => new TaskFlowRefItem
                {
                    task_id = x.task_id,
                    seq_num = x.seq_num,
                    process_code = x.process != null ? x.process.process_code : null,
                    process_name = x.process != null ? x.process.process_name : null
                })
                .ToListAsync(ct);

            if (flow.Count == 0)
                return null;

            var currentIndex = flow.FindIndex(x => x.task_id == currentTask.task_id);
            if (currentIndex <= 0)
                return null;

            var prev = flow[currentIndex - 1];

            return new PreviousProcessContext
            {
                previous_process_code = ProductionFlowHelper.Norm(prev.process_code),
                previous_stage_index = currentIndex - 1,
                route_process_codes = flow.Select(x => x.process_code).ToList()
            };
        }

        private async Task<material?> ResolveCoatingMaterialAsync(cost_estimate est, CancellationToken ct)
        {
            var codes = new List<string>
    {
        NormalizeMaterialCode(est.coating_type)
    };

            return await ResolveMaterialByCodesOrNamesAsync(ct, codes);
        }

        private async Task<material?> ResolveWaveMaterialAsync(cost_estimate est, CancellationToken ct)
        {
            var resolvedWaveType = EstimateMaterialAlternativeHelper.ResolveWaveType(
                est.wave_alternative,
                est.wave_type);

            var codes = new List<string>();

            codes.AddRange(BuildWaveCodeCandidates(resolvedWaveType));
            codes.AddRange(BuildWaveCodeCandidates(est.wave_type));
            codes.AddRange(BuildWaveCodeCandidates(est.wave_alternative));

            return await ResolveMaterialByCodesOrNamesAsync(ct, codes);
        }

        private static List<string> BuildWaveCodeCandidates(string? waveType)
        {
            var raw = (waveType ?? "").Trim().ToUpperInvariant()
                .Replace(" ", "_");

            var result = new List<string>();

            switch (raw)
            {
                case "SONG_B_NAU":
                    result.Add("SONG_B_NAU");
                    break;

                case "B":
                case "SONG_B":
                case "B_NAU":
                    result.Add("SONG_B_NAU");
                    break;

                case "SONG_E_MOC":
                case "E_MOC":
                    result.Add("SONG_E_MOC");
                    break;

                case "SONG_E_NAU":
                case "E_NAU":
                    result.Add("SONG_E_NAU");
                    break;

                case "E":
                case "SONG_E":
                    result.Add("SONG_E_NAU");
                    result.Add("SONG_E_MOC");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(raw))
                result.Add(raw);

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<material?> ResolvePaperMaterialAsync(cost_estimate est, CancellationToken ct)
        {
            var resolvedPaperCode = EstimateMaterialAlternativeHelper.ResolvePaperCode(
                est.paper_alternative,
                est.paper_code);

            return await ResolveMaterialByCodesOrNamesAsync(
                ct,
                new[]
                {
            resolvedPaperCode,
            est.paper_code,
            est.paper_alternative
                },
                new[]
                {
            est.paper_name,
            est.paper_alternative
                });
        }

        private async Task<material?> ResolveMaterialByCodesOrNamesAsync(
    CancellationToken ct,
    IEnumerable<string?> codeCandidates,
    IEnumerable<string?>? nameCandidates = null)
        {
            var aliases = new List<string>();

            aliases.AddRange(
                codeCandidates
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(NormalizeMaterialCode));

            if (nameCandidates != null)
            {
                aliases.AddRange(
                    nameCandidates
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(NormalizeMaterialCode));
            }

            aliases = aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return null;

            var allMaterials = await _db.materials
                .AsNoTracking()
                .ToListAsync(ct);

            foreach (var alias in aliases)
            {
                var matched = allMaterials.FirstOrDefault(m =>
                    string.Equals(NormalizeMaterialCode(m.code), alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeMaterialCode(m.name), alias, StringComparison.OrdinalIgnoreCase));

                if (matched != null)
                    return matched;
            }

            return null;
        }

        private async Task<material?> ResolveMaterialByCodesAsync(CancellationToken ct, params string?[] codes)
        {
            var normalized = codes
                .Select(NormalizeMaterialCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var code in normalized)
            {
                var mat = await _db.materials
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.code == code, ct);

                if (mat != null)
                    return mat;
            }

            return null;
        }

        private async Task<List<TaskConsumableMaterialDto>> GetConsumableMaterialsForTaskAsync(
    int taskId,
    CancellationToken ct = default)
        {
            var ctx = await GetTaskEstimateContextAsync(taskId, ct);
            if (ctx == null)
                return new List<TaskConsumableMaterialDto>();

            return await BuildConsumableMaterialsAsync(ctx, ct);
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static List<TaskMaterialUsageLogItemDto> BuildMaterialUsageSnapshot(
            List<TaskConsumableMaterialDto> expectedMaterials,
            List<TaskMaterialUsageInputDto> inputMaterials)
        {
            var result = new List<TaskMaterialUsageLogItemDto>();

            foreach (var expected in expectedMaterials)
            {
                if (!expected.material_id.HasValue || expected.material_id.Value <= 0)
                    throw new InvalidOperationException(
                        $"NVL {expected.material_code} chưa map material_id.");

                var input = inputMaterials.FirstOrDefault(x => x.material_id == expected.material_id.Value);
                if (input == null)
                    throw new InvalidOperationException(
                        $"Thiếu dữ liệu NVL material_id={expected.material_id.Value}.");

                var waste = expected.estimated_input_qty - input.quantity_used - input.quantity_left;
                if (waste < 0) waste = 0;

                result.Add(new TaskMaterialUsageLogItemDto
                {
                    material_id = expected.material_id.Value,
                    material_code = expected.material_code,
                    material_name = expected.material_name,
                    unit = expected.unit,

                    estimated_input_qty = Math.Round(expected.estimated_input_qty, 4),
                    quantity_used = Math.Round(input.quantity_used, 4),
                    quantity_left = Math.Round(input.quantity_left, 4),
                    quantity_waste = Math.Round(waste, 4),

                    is_stock = input.is_stock
                });
            }

            return result;
        }

        private sealed class TaskEstimateContext
        {
            public task Task { get; init; } = null!;
            public production Production { get; init; } = null!;
            public order_request Request { get; init; } = null!;
            public cost_estimate Estimate { get; init; } = null!;
        }
        private async Task<TaskEstimateContext?> GetTaskEstimateContextAsync(int taskId, CancellationToken ct = default)
        {
            var t = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (t == null || !t.prod_id.HasValue)
                return null;

            var prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == t.prod_id.Value, ct);

            if (prod?.order_id == null)
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
                        x.order_request_id == req.order_request_id, ct);
            }

            est ??= await _db.cost_estimates
                .AsNoTracking()
                .Where(x => x.order_request_id == req.order_request_id)
                .OrderByDescending(x => x.is_active)
                .ThenByDescending(x => x.estimate_id)
                .FirstOrDefaultAsync(ct);

            if (est == null)
                return null;

            return new TaskEstimateContext
            {
                Task = t,
                Production = prod,
                Request = req,
                Estimate = est
            };
        }

        private sealed class TaskFlowRefItem
        {
            public int task_id { get; init; }
            public int? seq_num { get; init; }
            public string? process_code { get; init; }
            public string? process_name { get; init; }
        }

        private static (string unit, decimal qty) ResolveReferenceInputShape(
    string? currentProcessCode,
    int currentStageIndex,
    IReadOnlyList<string?> routeProcessCodes,
    TaskEstimateContext ctx)
        {
            var req = ctx.Request;
            var est = ctx.Estimate;

            var orderQty = SafePositive(req.quantity ?? 0, 1);

            var sheetsRequired = Math.Max(est.sheets_required, 0);
            var sheetsWaste = Math.Max(est.sheets_waste, 0);
            var sheetsTotal = Math.Max(est.sheets_total, sheetsRequired + sheetsWaste);
            var nUp = SafePositive(est.n_up, 1);
            var numberOfPlates = SafePositive(req.number_of_plates ?? 0, 1);

            if (sheetsRequired <= 0)
                sheetsRequired = Math.Max(1, (int)Math.Ceiling(orderQty / (decimal)nUp));

            if (sheetsTotal <= 0)
                sheetsTotal = sheetsRequired + sheetsWaste;

            if (sheetsTotal <= 0)
                sheetsTotal = sheetsRequired;

            if (sheetsTotal <= 0)
                sheetsTotal = 1;

            var unit = StageQuantityHelper.ResolveQtyUnitLikeProduction(
                currentCode: currentProcessCode,
                currentStageIndex: currentStageIndex,
                routeProcessCodes: routeProcessCodes);

            var qty = StageQuantityHelper.GetProductionOutputCap(
                currentCode: currentProcessCode,
                currentStageIndex: currentStageIndex,
                routeProcessCodes: routeProcessCodes,
                sheetsTotal: sheetsTotal,
                nUp: nUp,
                numberOfPlates: numberOfPlates);

            return (unit, qty);
        }

        private static int SafePositive(int value, int fallback = 1)
    => value > 0 ? value : fallback;

        public async Task<List<TaskConsumableMaterialDto>> GetConsumableMaterialsForTaskPublicAsync(int taskId, CancellationToken ct = default)
        {
            return await GetConsumableMaterialsForTaskAsync(taskId, ct);
        }

        public async Task ValidateMaterialUsageForQrAsync(
    int taskId,
    List<TaskMaterialUsageInputDto> materials,
    CancellationToken ct = default)
        {
            var normalized = NormalizeMaterialUsageInputs(materials);

            var expectedMaterials = await GetConsumableMaterialsForTaskAsync(taskId, ct);

            var resolved = BuildReportMaterialUsageInputs(expectedMaterials, normalized);

            ValidateMaterialUsageInput(expectedMaterials, resolved);
        }

        private static List<TaskMaterialUsageInputDto> NormalizeMaterialUsageInputs(
    List<TaskMaterialUsageInputDto>? inputMaterials)
        {
            return (inputMaterials ?? new List<TaskMaterialUsageInputDto>())
                .Select(x => new TaskMaterialUsageInputDto
                {
                    material_id = x.material_id,
                    quantity_used = Math.Round(x.quantity_used, 4),
                    quantity_left = Math.Round(x.quantity_left, 4),
                    is_stock = x.is_stock
                })
                .ToList();
        }

        public async Task<List<TaskMaterialUsageInputDto>> BuildMaterialUsageForQrAsync(
            int taskId,
            List<TaskMaterialUsageInputDto>? materials,
            CancellationToken ct = default)
        {
            var normalized = NormalizeMaterialUsageInputs(materials);

            var expectedMaterials = await GetConsumableMaterialsForTaskAsync(taskId, ct);

            var resolved = BuildReportMaterialUsageInputs(expectedMaterials, normalized);

            ValidateMaterialUsageInput(expectedMaterials, resolved);

            return resolved;
        }

        public async Task<CancelTaskFinishResultDto> CancelTaskFinishAsync(
    int taskId,
    CancelTaskFinishRequest? req,
    int? cancelledByUserId,
    CancellationToken ct = default)
        {
            var reason = string.IsNullOrWhiteSpace(req?.reason)
                ? "Cancel task finish for recovery"
                : req!.reason!.Trim();

            return await _taskRepo.ExecuteInTransactionAsync(async innerCt =>
            {
                var t = await _db.tasks
                    .Include(x => x.process)
                    .FirstOrDefaultAsync(x => x.task_id == taskId, innerCt);

                if (t == null)
                    throw new KeyNotFoundException("Task not found.");

                if (!t.prod_id.HasValue || !t.seq_num.HasValue)
                    throw new InvalidOperationException("Task thiếu prod_id hoặc seq_num.");

                if (!string.Equals(t.status, "Finished", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Chỉ có thể recovery task đang ở trạng thái Finished.");

                // Chỉ cho cancel khi production/order đang InProcessing hoặc Importing.
                var statusCtx = await ValidateCancelableProductionAndOrderStatusAsync(t, innerCt);

                var flowTasks = await _db.tasks
                    .Include(x => x.process)
                    .Where(x => x.prod_id == t.prod_id.Value)
                    .OrderBy(x => x.seq_num)
                    .ThenBy(x => x.task_id)
                    .ToListAsync(innerCt);

                if (flowTasks.Count == 0)
                    throw new InvalidOperationException("Không tìm thấy flow task của production.");

                var currentSeq = t.seq_num.Value;

                var laterTasks = flowTasks
                    .Where(x => x.task_id != t.task_id)
                    .Where(x => (x.seq_num ?? int.MaxValue) > currentSeq)
                    .ToList();

                var laterTaskIds = laterTasks
                    .Select(x => x.task_id)
                    .ToList();

                var laterHasLog = laterTaskIds.Count > 0 &&
                    await _db.task_logs
                        .AsNoTracking()
                        .AnyAsync(x => x.task_id.HasValue &&
                                       laterTaskIds.Contains(x.task_id.Value), innerCt);

                var laterProgressed = laterTasks.Any(x =>
                    string.Equals(x.status, "Ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase) ||
                    x.start_time != null ||
                    x.end_time != null);

                if (laterProgressed || laterHasLog)
                {
                    throw new InvalidOperationException(
                        "Không thể recovery công đoạn này vì công đoạn phía sau đã bắt đầu hoặc đã có log. " +
                        "Hãy recovery từ công đoạn sau cùng trước.");
                }

                var finishLogs = await _db.task_logs
                    .Where(x => x.task_id == taskId &&
                                string.Equals(x.action_type, "Finished"))
                    .OrderBy(x => x.log_time)
                    .ToListAsync(innerCt);

                if (finishLogs.Count == 0)
                    throw new InvalidOperationException("Task đã Finished nhưng không tìm thấy task_log Finished để recovery.");

                // Chỉ được cancel trong vòng 5 phút sau khi task hoàn thành.
                EnsureCancelWithinFiveMinutes(t, finishLogs);

                var reversedStockMoveCount = await ReverseLeftoverStockFromTaskLogsAsync(
                    taskId,
                    finishLogs,
                    cancelledByUserId,
                    reason,
                    innerCt);

                _db.task_logs.RemoveRange(finishLogs);

                t.status = "Ready";
                t.end_time = null;

                t.start_time ??= AppTime.NowVnUnspecified();

                await ReserveMachineForRollbackReadyAsync(t, innerCt);

                var statusResult = await RollbackFinalTaskStatusesIfNeededAsync(
                    t,
                    flowTasks,
                    reason,
                    innerCt);

                await _db.SaveChangesAsync(innerCt);

                await _hub.Clients.All.SendAsync(
                    "update-ui",
                    new { message = "update UI" },
                    innerCt);

                return new CancelTaskFinishResultDto
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    deleted_log_count = finishLogs.Count,
                    reversed_stock_move_count = reversedStockMoveCount,
                    task_status = "Ready",
                    production_status = statusResult.productionStatus ?? statusCtx.prod.status,
                    order_status = statusResult.orderStatus ?? statusCtx.ord.status,
                    request_status = statusResult.requestStatus,
                    message = "Đã recovery task về Ready, xóa task_log Finished cũ và hoàn tác NVL dư đã nhập kho."
                };
            }, ct);
        }

        private static List<TaskMaterialUsageInputDto> BuildReportMaterialUsageInputs(
    List<TaskConsumableMaterialDto> expectedMaterials,
    List<TaskMaterialUsageInputDto> inputMaterials)
        {
            if (expectedMaterials.Any(x => !x.is_mapped))
            {
                var unmapped = string.Join(", ",
                    expectedMaterials
                        .Where(x => !x.is_mapped)
                        .Select(x => x.material_code));

                throw new InvalidOperationException(
                    $"Có NVL chưa map sang materials: {unmapped}. Không thể tạo QR/finish task.");
            }

            inputMaterials ??= new List<TaskMaterialUsageInputDto>();

            if (expectedMaterials.Count == 0)
            {
                if (inputMaterials.Count > 0)
                    throw new InvalidOperationException("Task này không yêu cầu nhập NVL.");

                return new List<TaskMaterialUsageInputDto>();
            }

            var duplicated = inputMaterials
                .GroupBy(x => x.material_id)
                .Any(g => g.Count() > 1);

            if (duplicated)
                throw new InvalidOperationException("Danh sách NVL bị trùng material_id.");

            var expectedMaterialIds = expectedMaterials
                .Where(x => x.material_id.HasValue && x.material_id.Value > 0)
                .Select(x => x.material_id!.Value)
                .ToHashSet();

            var unexpectedMaterialIds = inputMaterials
                .Where(x => !expectedMaterialIds.Contains(x.material_id))
                .Select(x => x.material_id)
                .Distinct()
                .ToList();

            if (unexpectedMaterialIds.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Có NVL không thuộc công đoạn này: material_id={string.Join(", ", unexpectedMaterialIds)}.");
            }

            var result = new List<TaskMaterialUsageInputDto>();

            foreach (var expected in expectedMaterials)
            {
                if (!expected.material_id.HasValue || expected.material_id.Value <= 0)
                    throw new InvalidOperationException($"NVL {expected.material_code} chưa có material_id hợp lệ.");

                var materialId = expected.material_id.Value;
                var expectedCode = NormalizeMaterialCode(expected.material_code);
                var isFixedPlate = expectedCode == "PLATE";

                var input = inputMaterials.FirstOrDefault(x => x.material_id == materialId);

                if (input == null && isFixedPlate)
                {
                    result.Add(new TaskMaterialUsageInputDto
                    {
                        material_id = materialId,
                        quantity_used = Math.Round(expected.estimated_input_qty, 4),
                        quantity_left = 0m,
                        is_stock = false
                    });

                    continue;
                }

                if (input == null)
                {
                    throw new InvalidOperationException(
                        $"Thiếu dữ liệu NVL {expected.material_code} - {expected.material_name}. " +
                        $"Công đoạn này bắt buộc nhập số lượng NVL dư.");
                }

                if (input.quantity_left < 0)
                {
                    throw new InvalidOperationException(
                        $"Số lượng NVL dư của {expected.material_code} không được nhỏ hơn 0.");
                }

                if (input.quantity_left > expected.estimated_input_qty)
                {
                    throw new InvalidOperationException(
                        $"Số lượng NVL dư của {expected.material_code} vượt số lượng input ước tính. " +
                        $"Left = {input.quantity_left}, Estimated = {expected.estimated_input_qty}");
                }

                var quantityLeft = Math.Round(input.quantity_left, 4);
                var quantityUsed = expected.estimated_input_qty - quantityLeft;

                if (quantityUsed < 0)
                    quantityUsed = 0;

                result.Add(new TaskMaterialUsageInputDto
                {
                    material_id = materialId,
                    quantity_used = Math.Round(quantityUsed, 4),
                    quantity_left = quantityLeft,
                    is_stock = quantityLeft > 0 || input.is_stock
                });
            }

            return result;
        }

        private async Task<material?> ResolveLaminationMaterialAsync(cost_estimate est, CancellationToken ct)
        {
            if (est.lamination_material_id.HasValue && est.lamination_material_id.Value > 0)
            {
                var byId = await _db.materials
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.material_id == est.lamination_material_id.Value, ct);

                if (byId != null)
                    return byId;
            }

            var hasSelectedLamination =
                !string.IsNullOrWhiteSpace(est.lamination_material_code) ||
                !string.IsNullOrWhiteSpace(est.lamination_material_name);

            if (!hasSelectedLamination)
                return null;

            return await ResolveMaterialByCodesOrNamesAsync(
                ct,
                new[]
                {
            est.lamination_material_code
                },
                new[]
                {
            est.lamination_material_name
                });
        }
        private static List<TaskMaterialUsageLogItemDto> ParseMaterialUsageLogItems(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<TaskMaterialUsageLogItemDto>();

            try
            {
                return JsonSerializer.Deserialize<List<TaskMaterialUsageLogItemDto>>(json, _jsonOptions)
                       ?? new List<TaskMaterialUsageLogItemDto>();
            }
            catch
            {
                return new List<TaskMaterialUsageLogItemDto>();
            }
        }

        private async Task<int> ReverseLeftoverStockFromTaskLogsAsync(
    int taskId,
    List<task_log> finishLogs,
    int? cancelledByUserId,
    string reason,
    CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var usageItems = finishLogs
                .SelectMany(x => ParseMaterialUsageLogItems(x.material_usage_json))
                .Where(x => x.material_id > 0)
                .Where(x => x.quantity_left > 0m)
                .Where(x => x.is_stock)
                .GroupBy(x => new
                {
                    x.material_id,
                    x.material_code,
                    x.material_name,
                    x.unit
                })
                .Select(g => new
                {
                    g.Key.material_id,
                    g.Key.material_code,
                    g.Key.material_name,
                    g.Key.unit,
                    quantity_left = Math.Round(g.Sum(x => x.quantity_left), 4)
                })
                .Where(x => x.quantity_left > 0m)
                .ToList();

            if (usageItems.Count == 0)
                return 0;

            var materialIds = usageItems
                .Select(x => x.material_id)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            foreach (var item in usageItems)
            {
                if (!materials.TryGetValue(item.material_id, out var mat))
                {
                    throw new InvalidOperationException(
                        $"Không tìm thấy NVL để hoàn tác nhập kho. material_id={item.material_id}");
                }

                var currentStock = mat.stock_qty ?? 0m;

                if (currentStock < item.quantity_left)
                {
                    throw new InvalidOperationException(
                        $"Không thể recovery task vì NVL dư đã được sử dụng/không còn đủ trong kho. " +
                        $"Material={mat.name} ({mat.code}), cần trừ lại={item.quantity_left}, tồn hiện tại={currentStock}");
                }
            }

            var reversedCount = 0;

            foreach (var item in usageItems)
            {
                var mat = materials[item.material_id];

                mat.stock_qty = (mat.stock_qty ?? 0m) - item.quantity_left;

                await _db.stock_moves.AddAsync(new stock_move
                {
                    material_id = item.material_id,
                    type = "OUT",
                    qty = item.quantity_left,
                    ref_doc = $"TASK-LEFTOVER-CANCEL-{taskId}",
                    user_id = cancelledByUserId,
                    move_date = now,
                    note = $"Cancel task finish. Reverse leftover stock from task_id={taskId}. Reason: {reason}"
                }, ct);

                reversedCount++;
            }

            return reversedCount;
        }

        private async Task ReserveMachineForRollbackReadyAsync(task t, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(t.machine))
                return;

            var machineCode = t.machine.Trim();

            var m = await _db.machines
                .FirstOrDefaultAsync(x => x.machine_code == machineCode && x.is_active, ct);

            if (m == null)
                return;

            m.busy_quantity ??= 0;
            m.free_quantity ??= m.quantity - m.busy_quantity.Value;

            if (m.free_quantity <= 0)
            {
                throw new InvalidOperationException(
                    $"Không thể recovery task về Ready vì máy {machineCode} hiện không còn slot rảnh để giữ lại cho công đoạn.");
            }

            m.free_quantity -= 1;
            m.busy_quantity += 1;

            if (m.free_quantity < 0)
                m.free_quantity = 0;
        }

        private async Task<(string? productionStatus, string? orderStatus, string? requestStatus)>
    RollbackFinalTaskStatusesIfNeededAsync(
        task currentTask,
        List<task> flowTasks,
        string reason,
        CancellationToken ct)
        {
            if (!currentTask.prod_id.HasValue || !currentTask.seq_num.HasValue)
                return (null, null, null);

            var currentSeq = currentTask.seq_num.Value;

            var isLastTask = !flowTasks.Any(x =>
                x.task_id != currentTask.task_id &&
                (x.seq_num ?? int.MaxValue) > currentSeq);

            if (!isLastTask)
                return (null, null, null);

            var prod = await _db.productions
                .FirstOrDefaultAsync(x => x.prod_id == currentTask.prod_id.Value, ct);

            if (prod == null)
                return (null, null, null);

            if (!prod.order_id.HasValue)
                throw new InvalidOperationException("Production chưa gắn với order.");

            var ord = await _db.orders
                .FirstOrDefaultAsync(x => x.order_id == prod.order_id.Value, ct);

            if (ord == null)
                throw new InvalidOperationException("Không tìm thấy order của production.");

            if (!IsCancelableProductionStatus(prod.status))
            {
                throw new InvalidOperationException(
                    $"Không thể recovery công đoạn cuối. Production phải đang InProcessing hoặc Importing. " +
                    $"Hiện tại: {ShowStatus(prod.status)}.");
            }

            if (!IsCancelableProductionStatus(ord.status))
            {
                throw new InvalidOperationException(
                    $"Không thể recovery công đoạn cuối. Order phải đang InProcessing hoặc Importing. " +
                    $"Hiện tại: {ShowStatus(ord.status)}.");
            }

            if (string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase))
            {
                prod.status = "InProcessing";
            }

            prod.end_date = null;

            if (string.Equals(ord.status, "Importing", StringComparison.OrdinalIgnoreCase))
            {
                ord.status = "InProcessing";
            }

            string? requestStatus = null;

            var requests = await _db.order_requests
                .Where(x => x.order_id == ord.order_id)
                .ToListAsync(ct);

            foreach (var request in requests)
            {
                if (string.Equals(request.process_status, "Importing", StringComparison.OrdinalIgnoreCase))
                {
                    request.process_status = "InProcessing";
                    requestStatus = "InProcessing";
                }
            }

            return (prod.status, ord.status, requestStatus);
        }

        private static bool IsCancelableProductionStatus(string? status)
        {
            return string.Equals(status, "InProcessing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Importing", StringComparison.OrdinalIgnoreCase);
        }

        private static string ShowStatus(string? status)
        {
            return string.IsNullOrWhiteSpace(status) ? "(null)" : status.Trim();
        }

        private static DateTime ResolveFinishedAtOrThrow(
            task t,
            List<task_log> finishLogs)
        {
            if (t.end_time.HasValue)
                return t.end_time.Value;

            var latestLogTime = finishLogs
                .Where(x => x.log_time.HasValue)
                .Select(x => x.log_time!.Value)
                .OrderByDescending(x => x)
                .FirstOrDefault();

            if (latestLogTime == default)
                throw new InvalidOperationException(
                    "Không xác định được thời điểm hoàn thành task nên không thể cancel finish.");

            return latestLogTime;
        }

        private static void EnsureCancelWithinFiveMinutes(
            task t,
            List<task_log> finishLogs)
        {
            var finishedAt = ResolveFinishedAtOrThrow(t, finishLogs);
            var now = AppTime.NowVnUnspecified();

            var deadline = finishedAt.AddMinutes(5);

            if (now > deadline)
            {
                throw new InvalidOperationException(
                    $"Không thể cancel finish vì đã quá 5 phút kể từ khi task hoàn thành. " +
                    $"FinishedAt={finishedAt:yyyy-MM-dd HH:mm:ss}, " +
                    $"Deadline={deadline:yyyy-MM-dd HH:mm:ss}, " +
                    $"Now={now:yyyy-MM-dd HH:mm:ss}.");
            }
        }

        private async Task<(production prod, order ord)> ValidateCancelableProductionAndOrderStatusAsync(
            task t,
            CancellationToken ct)
        {
            if (!t.prod_id.HasValue)
                throw new InvalidOperationException("Task chưa gắn với production.");

            var prod = await _db.productions
                .FirstOrDefaultAsync(x => x.prod_id == t.prod_id.Value, ct);

            if (prod == null)
                throw new InvalidOperationException("Không tìm thấy production của task.");

            if (!prod.order_id.HasValue)
                throw new InvalidOperationException("Production chưa gắn với order.");

            var ord = await _db.orders
                .FirstOrDefaultAsync(x => x.order_id == prod.order_id.Value, ct);

            if (ord == null)
                throw new InvalidOperationException("Không tìm thấy order của production.");

            if (!IsCancelableProductionStatus(prod.status))
            {
                throw new InvalidOperationException(
                    $"Chỉ được cancel finish khi production đang InProcessing hoặc Importing. " +
                    $"Trạng thái production hiện tại: {ShowStatus(prod.status)}.");
            }

            if (!IsCancelableProductionStatus(ord.status))
            {
                throw new InvalidOperationException(
                    $"Chỉ được cancel finish khi order đang InProcessing hoặc Importing. " +
                    $"Trạng thái order hiện tại: {ShowStatus(ord.status)}.");
            }

            return (prod, ord);
        }

        private sealed class BothConsumableScaleContext
        {
            public bool ShouldScale { get; init; }
            public decimal Ratio { get; init; } = 1m;
        }

        private static string NormBothProcessCodeForScan(string? code)
        {
            return (code ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static HashSet<string> ParseBothProcessCodesForScan(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormBothProcessCodeForScan)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<BothConsumableScaleContext> ResolveBothConsumableScaleAsync(
            TaskEstimateContext ctx,
            CancellationToken ct)
        {
            var prod = ctx.Production;

            if (!string.Equals(prod.prod_method, "BOTH", StringComparison.OrdinalIgnoreCase))
                return new BothConsumableScaleContext();

            if (!prod.sub_product_id.HasValue || prod.sub_product_id.Value <= 0)
                return new BothConsumableScaleContext();

            var currentCode = NormBothProcessCodeForScan(ctx.Task.process?.process_code);

            // Không scale bản kẽm.
            if (currentCode == "RALO")
                return new BothConsumableScaleContext();

            var orderQty = ctx.Request.quantity ?? 0;
            if (orderQty <= 0)
                orderQty = 1;

            var nvlQty = prod.nvl_qty > 0
                ? prod.nvl_qty
                : Math.Max(orderQty - prod.sub_product_used_qty, 0);

            if (nvlQty <= 0)
                return new BothConsumableScaleContext();

            var route = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .Where(x => x.prod_id == prod.prod_id)
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.task_id)
                .ToListAsync(ct);

            var currentIndex = route.FindIndex(x => x.task_id == ctx.Task.task_id);
            if (currentIndex < 0)
                return new BothConsumableScaleContext();

            var subProcess = await _db.sub_products
                .AsNoTracking()
                .Where(x => x.id == prod.sub_product_id.Value)
                .Select(x => x.product_process)
                .FirstOrDefaultAsync(ct);

            var subCodes = ParseBothProcessCodesForScan(subProcess);
            if (subCodes.Count == 0)
                return new BothConsumableScaleContext();

            var subLastIndex = -1;

            for (var i = 0; i < route.Count; i++)
            {
                var routeCode = NormBothProcessCodeForScan(route[i].process?.process_code);
                if (subCodes.Contains(routeCode))
                    subLastIndex = i;
            }

            if (subLastIndex < 0 || currentIndex > subLastIndex)
                return new BothConsumableScaleContext();

            return new BothConsumableScaleContext
            {
                ShouldScale = true,
                Ratio = Math.Clamp((decimal)nvlQty / orderQty, 0m, 1m)
            };
        }

        private async Task<bool> IsGroupTaskAsync(int taskId, CancellationToken ct)
        {
            return await _db.tasks
                .AsNoTracking()
                .Include(x => x.prod)
                .AnyAsync(x =>
                    x.task_id == taskId &&
                    x.prod != null &&
                    x.prod.prod_kind == "GROUP", ct);
        }

        private async Task<bool> ShouldUseManualReportInputAsync(
            int taskId,
            ScanTaskRequest req,
            CancellationToken ct)
        {
            if (req.use_manual_input)
                return true;

            var t = await _db.tasks
                .AsNoTracking()
                .Include(x => x.prod)
                .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

            if (t == null)
                return false;

            if (string.Equals(t.input_mode, "MANUAL", StringComparison.OrdinalIgnoreCase))
                return true;

            if (t.prod != null &&
                string.Equals(t.prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static void ValidateManualMaterialUsageInput(List<TaskMaterialUsageInputDto> materials)
        {
            if (materials == null || materials.Count == 0)
                return;

            var duplicated = materials
                .GroupBy(x => x.material_id)
                .Any(x => x.Count() > 1);

            if (duplicated)
                throw new InvalidOperationException("Danh sách NVL nhập tay bị trùng material_id.");

            foreach (var item in materials)
            {
                if (item.material_id <= 0)
                    throw new InvalidOperationException("material_id không hợp lệ.");

                if (item.quantity_used < 0)
                    throw new InvalidOperationException("quantity_used không được nhỏ hơn 0.");

                if (item.quantity_left < 0)
                    throw new InvalidOperationException("quantity_left không được nhỏ hơn 0.");

                if (item.quantity_left > 0 && !item.is_stock)
                    throw new InvalidOperationException("NVL có quantity_left > 0 thì is_stock phải = true.");
            }
        }

        private async Task<List<TaskMaterialUsageLogItemDto>> BuildManualMaterialUsageSnapshotAsync(
            List<TaskMaterialUsageInputDto> inputMaterials,
            CancellationToken ct)
        {
            var result = new List<TaskMaterialUsageLogItemDto>();

            if (inputMaterials == null || inputMaterials.Count == 0)
                return result;

            var materialIds = inputMaterials
                .Select(x => x.material_id)
                .Distinct()
                .ToList();

            var materials = await _db.materials
                .AsNoTracking()
                .Where(x => materialIds.Contains(x.material_id))
                .ToDictionaryAsync(x => x.material_id, ct);

            foreach (var input in inputMaterials)
            {
                if (!materials.TryGetValue(input.material_id, out var mat))
                    throw new InvalidOperationException($"Không tìm thấy NVL material_id={input.material_id}.");

                result.Add(new TaskMaterialUsageLogItemDto
                {
                    material_id = mat.material_id,
                    material_code = mat.code,
                    material_name = mat.name,
                    unit = mat.unit,
                    estimated_input_qty = Math.Round(input.quantity_used + input.quantity_left, 4),
                    quantity_used = Math.Round(input.quantity_used, 4),
                    quantity_left = Math.Round(input.quantity_left, 4),
                    quantity_waste = 0m,
                    is_stock = input.is_stock
                });
            }

            return result;
        }

        private static List<TaskReferenceUsageInputDto> NormalizeReferenceInputs(
            List<TaskReferenceUsageInputDto>? inputs)
        {
            return (inputs ?? new List<TaskReferenceUsageInputDto>())
                .Where(x => !string.IsNullOrWhiteSpace(x.input_code))
                .Select(x => new TaskReferenceUsageInputDto
                {
                    input_code = x.input_code.Trim(),
                    input_name = string.IsNullOrWhiteSpace(x.input_name) ? null : x.input_name.Trim(),
                    unit = string.IsNullOrWhiteSpace(x.unit) ? null : x.unit.Trim(),
                    quantity_used = Math.Round(x.quantity_used, 4),
                    quantity_left = Math.Round(x.quantity_left, 4)
                })
                .ToList();
        }

        private static List<TaskOutputReportDto> NormalizeOutputs(
            List<TaskOutputReportDto>? outputs,
            string? fallbackCode,
            string? fallbackName,
            string? fallbackUnit,
            int qtyGood)
        {
            var list = (outputs ?? new List<TaskOutputReportDto>())
                .Where(x => !string.IsNullOrWhiteSpace(x.output_code))
                .Select(x => new TaskOutputReportDto
                {
                    output_code = x.output_code.Trim(),
                    output_name = string.IsNullOrWhiteSpace(x.output_name) ? null : x.output_name.Trim(),
                    unit = string.IsNullOrWhiteSpace(x.unit) ? null : x.unit.Trim(),
                    quantity_good = Math.Round(x.quantity_good, 4),
                    quantity_bad = Math.Round(x.quantity_bad, 4)
                })
                .ToList();

            if (list.Count == 0)
            {
                list.Add(new TaskOutputReportDto
                {
                    output_code = fallbackCode ?? "OUTPUT",
                    output_name = fallbackName ?? "Output",
                    unit = fallbackUnit ?? "sp",
                    quantity_good = qtyGood,
                    quantity_bad = 0
                });
            }

            foreach (var item in list)
            {
                if (item.quantity_good < 0)
                    throw new InvalidOperationException("quantity_good output không được nhỏ hơn 0.");

                if (item.quantity_bad < 0)
                    throw new InvalidOperationException("quantity_bad output không được nhỏ hơn 0.");
            }

            return list;
        }
    }
}