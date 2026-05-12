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

            var rawQrMaterials = NormalizeMaterialUsageInputs(payload.materials);

            var expectedMaterials = await GetConsumableMaterialsForTaskAsync(taskId, ct);

            var qrMaterials = BuildReportMaterialUsageInputs(expectedMaterials, rawQrMaterials);

            ValidateMaterialUsageInput(expectedMaterials, qrMaterials);

            var materialUsageSnapshot = BuildMaterialUsageSnapshot(expectedMaterials, qrMaterials);

            var materialUsageJson = materialUsageSnapshot.Count == 0
                ? null
                : JsonSerializer.Serialize(materialUsageSnapshot, _jsonOptions);

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

            var result = await _taskRepo.ExecuteInTransactionAsync(async innerCt =>
            {
                var t = await _taskRepo.GetByIdAsync(taskId)
                    ?? throw new Exception("Task not found");

                if (!t.prod_id.HasValue || !t.seq_num.HasValue)
                    throw new Exception("Task missing prod_id/seq_num");

                var flowTasks = await _taskRepo.GetTasksByProductionWithProcessAsync(t.prod_id.Value, innerCt);
                var currentFlow = flowTasks.FirstOrDefault(x => x.task_id == t.task_id)
                    ?? throw new Exception("Task flow info not found");

                var currentCode = ProductionFlowHelper.Norm(currentFlow.process?.process_code);
                var hasRalo = flowTasks.Any(x => ProductionFlowHelper.IsRalo(x.process?.process_code));
                var raloFinished = !hasRalo || flowTasks.Any(x =>
                    ProductionFlowHelper.IsRalo(x.process?.process_code) &&
                    string.Equals(x.status, "Finished", StringComparison.OrdinalIgnoreCase));

                if (!hasRalo)
                {
                    var prev = await _taskRepo.GetPrevTaskAsync(t.prod_id.Value, t.seq_num.Value);
                    if (prev != null && !string.Equals(prev.status, "Finished", StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Previous step (task_id={prev.task_id}, seq={prev.seq_num}) is not Finished");
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

                var finishLog = new task_log
                {
                    task_id = t.task_id,
                    scanned_code = req.token,
                    action_type = "Finished",
                    qty_good = qtyGood,
                    log_time = now,
                    scanned_by_user_id = scannedByUserId,
                    material_usage_json = materialUsageJson,
                    reason = NormalizeNullableText(req.reason, 1000),
                    report_image_url = NormalizeNullableText(req.report_image_url, 8000)
                };
                await _logRepo.AddAsync(finishLog);

                foreach (var item in materialUsageSnapshot)
                {
                    if (item.quantity_left > 0 && item.is_stock)
                    {
                        var mat = await _db.materials
                            .FirstOrDefaultAsync(x => x.material_id == item.material_id, innerCt);

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
                            purchase_id = null
                        }, innerCt);
                    }
                }

                await _taskRepo.SaveChangesAsync(innerCt);

                if (t.prod_id.HasValue)
                    await _prodRepo.TryCloseProductionIfCompletedAsync(t.prod_id.Value, now, innerCt);

                production? prod = null;
                order? ord = null;

                if (t.prod_id.HasValue)
                    prod = await _prodRepo.GetByIdForUpdateAsync(t.prod_id.Value, innerCt);

                if (prod != null
                    && string.Equals(prod.status, "Importing", StringComparison.OrdinalIgnoreCase)
                    && prod.order_id.HasValue)
                {
                    ord = await _orderRepo.GetByIdForUpdateAsync(prod.order_id.Value, innerCt);

                    if (ord != null &&
                        !string.Equals(ord.status, "Importing", StringComparison.OrdinalIgnoreCase))
                    {
                        ord.status = "Importing";
                        var request = await _db.order_requests.FirstOrDefaultAsync(o => o.order_id == ord.order_id, innerCt);

                        await _hub.Clients.All.SendAsync("finishedProduction",
                            new { message = $"Đơn hàng {prod.order_id} đã được sản xuất xong" }, innerCt);

                        if (request != null)
                        {
                            //await _hub.Clients.Group(RealtimeGroups.ByRole("warehouse manager")).SendAsync(
                            //    "Importing",
                            //    new { message = $"Đơn hàng {ord.order_id} đã được sản xuất xong, chờ nhập kho" },
                            //    innerCt);
                            await _hub.Clients.Group(RealtimeGroups.ByRole("general manager")).SendAsync(
                                "Importing",
                                new { message = $"Đơn hàng {ord.order_id} đã được sản xuất xong, chờ nhập kho" },
                                innerCt);

                            await _noti.CreateNotfi(
                                4,
                                $"Đơn hàng {ord.order_id} đã được sản xuất xong, chờ nhập kho",
                                null,
                                request.order_request_id,
                                "Importing");
                            await _noti.CreateNotfi(
                                18,
                                $"Đơn hàng {ord.order_id} đã được sản xuất xong, chờ nhập kho",
                                null,
                                request.order_request_id,
                                "Importing");
                        }
                    }

                    if (ord != null)
                    {
                        await _orderRequestRepo.MarkProcessStatusImportingByOrderAsync(ord.order_id, ord.quote_id, innerCt);
                        await _orderRequestRepo.MarkProcessStatusImportingByOrderAsync(ord.order_id, null, innerCt);
                    }
                }

                await _hub.Clients.All.SendAsync("update-ui", new { message = "update UI" }, innerCt);

                return new ScanTaskResult
                {
                    task_id = t.task_id,
                    prod_id = t.prod_id,
                    message = $"Finished & logged qty_good={qtyGood}. Đã lưu log NVL sử dụng/hoàn kho."
                };
            }, ct);

            if (result.prod_id.HasValue)
            {
                var prod = await _prodRepo.GetByIdForUpdateAsync(result.prod_id.Value, ct);
                if (prod?.order_id != null)
                {
                    await _hub.Clients.All.SendAsync("finishedProduction",
                        new { message = $"Đơn hàng {prod.order_id} đã được sản xuất xong" }, ct);
                }
            }

            return result;
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

        public async Task<TaskQrMaterialBundleDto> GetTaskQrMaterialBundleAsync(int taskId, CancellationToken ct = default)
        {
            var ctx = await GetTaskEstimateContextAsync(taskId, ct);
            if (ctx == null)
                return new TaskQrMaterialBundleDto();

            return new TaskQrMaterialBundleDto
            {
                consumable_materials = await BuildConsumableMaterialsAsync(ctx, ct),
                reference_inputs = await BuildReferenceInputsAsync(ctx, ct)
            };
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
                    note = $"Cancel task finish. Reverse leftover stock from task_id={taskId}. Reason: {reason}",
                    purchase_id = null
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
    }
}