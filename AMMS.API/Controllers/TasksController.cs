using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using static AMMS.Shared.DTOs.Productions.TaskQrResponse;

[ApiController]
[Route("api/[controller]")]
public class TasksController : ControllerBase
{
    private readonly ITaskRepository _taskRepo;
    private readonly ITaskQrTokenService _tokenSvc;
    private readonly ITaskScanService _scanSvc;
    private readonly ITaskService _taskService;
    private readonly AppDbContext _db;
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly ICloudinaryFileStorageService _fileStorage;
    private readonly IConfiguration _configuration;

    public TasksController(
        AppDbContext db,
        IHubContext<RealtimeHub> hub,
        ITaskRepository taskRepo,
        ITaskQrTokenService tokenSvc,
        ITaskScanService scanSvc,
        ITaskService taskService,
        ICloudinaryFileStorageService fileStorage,
        IConfiguration configuration)
    {
        _db = db;
        _taskRepo = taskRepo;
        _tokenSvc = tokenSvc;
        _scanSvc = scanSvc;
        _taskService = taskService;
        _hub = hub;
        _fileStorage = fileStorage;
        _configuration = configuration;
    }

    private int? GetCurrentUserId()
    {
        var raw =
            User.FindFirst("userid")?.Value ??
            User.FindFirst("user_id")?.Value ??
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return int.TryParse(raw, out var userId) ? userId : null;
    }

    [HttpGet("qr-prepare/{taskId:int}")]
    public async Task<IActionResult> GetQrPrepare(int taskId, CancellationToken ct)
    {
        if (taskId <= 0)
        {
            return BadRequest(new
            {
                message = "task_id không hợp lệ.",
                task_id = taskId
            });
        }

        var taskForPrepare = await _db.tasks
            .AsNoTracking()
            .Include(x => x.process)
            .Include(x => x.prod)
            .FirstOrDefaultAsync(x => x.task_id == taskId, ct);

        if (taskForPrepare == null)
        {
            return NotFound(new
            {
                message = "Task not found",
                task_id = taskId
            });
        }

        var resolved = await ResolveQrPrepareUnifiedPolicyAsync(
            taskForPrepare,
            ct);

        return Ok(new
        {
            task_id = taskId,

            process_code = resolved.process_code,
            process_name = resolved.process_name,

            qty_unit = resolved.qty_unit,
            min_allowed = resolved.min_allowed,
            max_allowed = resolved.max_allowed,
            suggested_qty = resolved.suggested_qty,
            happy_case_qty = resolved.happy_case_qty,

            order_qty = resolved.order_qty,
            sheets_required = resolved.sheets_required,
            sheets_waste = resolved.sheets_waste,
            sheets_total = resolved.sheets_total,
            n_up = resolved.n_up,
            number_of_plates = resolved.number_of_plates,

            stage_index = resolved.stage_index,
            stage_count = resolved.stage_count,

            production_output_qty = resolved.production_output_qty,
            production_output_unit = resolved.production_output_unit,

            input_mode = resolved.input_mode,
            allow_manual_input = resolved.allow_manual_input,
            can_use_manual_input = true,
            manual_input_optional = resolved.manual_input_optional,

            is_group_production = resolved.is_group_production,
            group_prod_id = resolved.group_prod_id,
            group_total_qty = resolved.group_total_qty,

            manual_input_hint = resolved.manual_input_hint,

            consumable_materials = resolved.consumable_materials,
            reference_inputs = resolved.reference_inputs
        });
    }

    private sealed class QrPrepareUnifiedPolicy
    {
        public string? process_code { get; set; }

        public string? process_name { get; set; }

        public string qty_unit { get; set; } = "sp";

        public int min_allowed { get; set; } = 1;

        public int max_allowed { get; set; } = 1;

        public int suggested_qty { get; set; } = 1;

        public int happy_case_qty { get; set; } = 1;

        public int order_qty { get; set; }

        public int sheets_required { get; set; }

        public int sheets_waste { get; set; }

        public int sheets_total { get; set; }

        public int n_up { get; set; } = 1;

        public int number_of_plates { get; set; }

        public int stage_index { get; set; }

        public int stage_count { get; set; }

        public int production_output_qty { get; set; }

        public string production_output_unit { get; set; } = "sp";

        public string input_mode { get; set; } = "ESTIMATE";

        public bool allow_manual_input { get; set; }

        public bool manual_input_optional { get; set; } = true;

        public bool is_group_production { get; set; }

        public int? group_prod_id { get; set; }

        public int? group_total_qty { get; set; }

        public string manual_input_hint { get; set; } = "";

        public List<TaskConsumableMaterialDto> consumable_materials { get; set; } = new();

        public List<TaskReferenceInputDto> reference_inputs { get; set; } = new();
    }

    private sealed class GroupQrComposition
    {
        public int link_qty_plan { get; set; }

        public List<string> member_methods { get; set; } = new();

        public bool has_nvl { get; set; }

        public bool has_sub { get; set; }

        public bool has_both { get; set; }

        public string case_label { get; set; } = "";
    }

    private async Task<QrPrepareUnifiedPolicy> ResolveQrPrepareUnifiedPolicyAsync(
        task taskMeta,
        CancellationToken ct)
    {
        if (taskMeta == null)
            throw new InvalidOperationException("Task not found.");

        if (!taskMeta.prod_id.HasValue)
            throw new InvalidOperationException("Task chưa gắn với production.");

        var isGroup =
            taskMeta.prod != null &&
            string.Equals(taskMeta.prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase);

        return isGroup
            ? await ResolveGroupQrPrepareUnifiedPolicyAsync(taskMeta, ct)
            : await ResolveSingleQrPrepareUnifiedPolicyAsync(taskMeta, ct);
    }

    private async Task<QrPrepareUnifiedPolicy> ResolveGroupQrPrepareUnifiedPolicyAsync(
        task taskMeta,
        CancellationToken ct)
    {
        if (taskMeta.prod == null)
        {
            taskMeta.prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == taskMeta.prod_id, ct);
        }

        if (taskMeta.prod == null)
            throw new InvalidOperationException("Không tìm thấy production của group task.");

        var prod = taskMeta.prod;

        var bundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
            taskMeta.task_id,
            ct);

        var composition = await ResolveGroupQrCompositionAsync(
            taskMeta,
            ct);

        var groupStageCount = await _db.tasks
            .AsNoTracking()
            .CountAsync(x => x.prod_id == prod.prod_id, ct);

        var processCode = NormQrProcessCode(taskMeta.process?.process_code);
        var processName = taskMeta.process?.process_name;

        var groupTotalQty = prod.group_total_qty > 0
            ? prod.group_total_qty
            : 0;

        var linkQtyPlan = composition.link_qty_plan > 0
            ? composition.link_qty_plan
            : 0;

        /*
         * Source chính để đồng bộ:
         * - reference_inputs.actual_qty_prev_stage: số BTP/input thực tế cần dùng.
         * - Nếu actual chưa có thì fallback estimated.
         *
         * SUB-SUB / SUB-NVL:
         * TaskScanService.GetTaskQrMaterialBundleAsync phải build reference_inputs
         * theo order qty + hao phí downstream.
         */
        var referenceQty = ResolveMainReferenceQtyForPrepare(
            bundle.reference_inputs);

        var effectiveQtyDecimal =
            referenceQty > 0 ? referenceQty :
            linkQtyPlan > 0 ? linkQtyPlan :
            groupTotalQty > 0 ? groupTotalQty :
            1m;

        var effectiveQty = CeilPositive(effectiveQtyDecimal);

        if (effectiveQty <= 0)
            effectiveQty = 1;

        var referenceInputs = NormalizeReferenceInputsForQrPrepare(
            bundle.reference_inputs,
            effectiveQty,
            forceActualTotal: referenceQty > 0);

        var qtyUnit = ResolveQrUnit(
            processCode,
            referenceInputs,
            fallback: "sp");

        return new QrPrepareUnifiedPolicy
        {
            process_code = taskMeta.process?.process_code,
            process_name = processName,

            qty_unit = qtyUnit,
            min_allowed = 1,
            max_allowed = effectiveQty,
            suggested_qty = effectiveQty,
            happy_case_qty = effectiveQty,

            order_qty = groupTotalQty > 0
                ? groupTotalQty
                : effectiveQty,

            sheets_required = 0,
            sheets_waste = 0,
            sheets_total = 0,
            n_up = 1,
            number_of_plates = 0,

            stage_index = taskMeta.seq_num ?? 1,
            stage_count = groupStageCount,

            production_output_qty = effectiveQty,
            production_output_unit = qtyUnit,

            input_mode = "MANUAL",
            allow_manual_input = true,
            manual_input_optional = false,

            is_group_production = true,
            group_prod_id = prod.prod_id,
            group_total_qty = groupTotalQty,

            manual_input_hint =
                $"GROUP {composition.case_label}: QR prepare đã đồng bộ theo reference input thực tế. " +
                $"group_total_qty={groupTotalQty}; task_link_qty_plan={linkQtyPlan}; " +
                $"reference_qty={Math.Round(referenceQty, 4)}; effective_qty={effectiveQty}. " +
                $"SUB-SUB/SUB-NVL dùng số BTP sau hao phí downstream, không dùng sheets_required/sheets_total của NVL estimate.",

            consumable_materials = bundle.consumable_materials ?? new List<TaskConsumableMaterialDto>(),
            reference_inputs = referenceInputs
        };
    }

    private async Task<QrPrepareUnifiedPolicy> ResolveSingleQrPrepareUnifiedPolicyAsync(
        task taskMeta,
        CancellationToken ct)
    {
        var policy = await _taskRepo.GetQtyPolicyAsync(
            taskMeta.task_id,
            ct);

        if (policy == null)
            throw new InvalidOperationException("Không xác định được policy số lượng cho task.");

        var bundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
            taskMeta.task_id,
            ct);

        var inputMode = string.IsNullOrWhiteSpace(taskMeta.input_mode)
            ? "ESTIMATE"
            : taskMeta.input_mode.Trim();

        var forceManual = string.Equals(
            inputMode,
            "MANUAL",
            StringComparison.OrdinalIgnoreCase);

        var processCode = NormQrProcessCode(policy.process_code);

        /*
         * Main reference qty:
         * - Loại PLATE_FROM_RALO để không cộng bản kẽm vào số lượng sản phẩm.
         * - Ưu tiên actual_qty_prev_stage.
         * - Nếu actual chưa có thì dùng estimated_qty.
         */
        var referenceQty = ResolveMainReferenceQtyForPrepare(
            bundle.reference_inputs);

        var canOverrideByReference = CanQrPrepareUseReferenceQty(
            processCode,
            bundle.reference_inputs);

        var baseSuggested = policy.suggested_qty > 0
            ? policy.suggested_qty
            : 1;

        var baseMax = policy.max_allowed > 0
            ? policy.max_allowed
            : baseSuggested;

        var effectiveQty = baseSuggested;

        if (canOverrideByReference && referenceQty > 0)
        {
            effectiveQty = CeilPositive(referenceQty);
        }

        if (effectiveQty <= 0)
            effectiveQty = 1;

        /*
         * FIX chính:
         * Với SUB single full path / SPLIT / downstream sau BTP:
         * max_allowed không được nhỏ hơn reference actual.
         *
         * Ví dụ BOI:
         * policy.max_allowed = 1329 nhưng reference actual CAN = 3942
         * => max_allowed phải thành 3942.
         */
        var maxAllowed = Math.Max(baseMax, effectiveQty);

        var referenceInputs = NormalizeReferenceInputsForQrPrepare(
            bundle.reference_inputs,
            effectiveQty,
            forceActualTotal: canOverrideByReference && referenceQty > 0);

        var qtyUnit = ResolveQrUnit(
            processCode,
            referenceInputs,
            fallback: policy.qty_unit);

        return new QrPrepareUnifiedPolicy
        {
            process_code = policy.process_code,
            process_name = policy.process_name,

            qty_unit = qtyUnit,
            min_allowed = policy.min_allowed > 0 ? policy.min_allowed : 1,
            max_allowed = maxAllowed,
            suggested_qty = effectiveQty,
            happy_case_qty = effectiveQty,

            order_qty = policy.order_qty,
            sheets_required = policy.sheets_required,
            sheets_waste = policy.sheets_waste,
            sheets_total = policy.sheets_total,
            n_up = policy.n_up,
            number_of_plates = policy.number_of_plates,

            stage_index = policy.stage_index,
            stage_count = policy.stage_count,

            production_output_qty = effectiveQty,
            production_output_unit = qtyUnit,

            input_mode = inputMode,
            allow_manual_input = forceManual,
            manual_input_optional = !forceManual,

            is_group_production = false,
            group_prod_id = null,
            group_total_qty = null,

            manual_input_hint =
                forceManual
                    ? $"Task MANUAL. QR prepare dùng policy đồng bộ. base_max={baseMax}; reference_qty={Math.Round(referenceQty, 4)}; effective_qty={effectiveQty}."
                    : $"Task SINGLE/SPLIT. QR prepare dùng policy đồng bộ. base_max={baseMax}; reference_qty={Math.Round(referenceQty, 4)}; effective_qty={effectiveQty}.",

            consumable_materials = bundle.consumable_materials ?? new List<TaskConsumableMaterialDto>(),
            reference_inputs = referenceInputs
        };
    }

    private async Task<GroupQrComposition> ResolveGroupQrCompositionAsync(
        task groupTask,
        CancellationToken ct)
    {
        var result = new GroupQrComposition();

        var links = await _db.task_links
            .AsNoTracking()
            .Where(x =>
                x.group_task_id == groupTask.task_id &&
                (
                    x.status == null ||
                    x.status.ToUpper() != "CANCELLED"
                ))
            .ToListAsync(ct);

        result.link_qty_plan = links.Sum(x => Math.Max(x.qty_plan, 0));

        var singleProdIds = links
            .Where(x => x.single_prod_id > 0)
            .Select(x => x.single_prod_id)
            .Distinct()
            .ToList();

        if (singleProdIds.Count == 0)
        {
            result.case_label = "NO_LINK";
            return result;
        }

        var methods = await _db.productions
            .AsNoTracking()
            .Where(x => singleProdIds.Contains(x.prod_id))
            .Select(x => x.prod_method)
            .ToListAsync(ct);

        result.member_methods = methods
            .Select(NormQrMethod)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.has_nvl = result.member_methods.Contains("NVL", StringComparer.OrdinalIgnoreCase);
        result.has_sub = result.member_methods.Contains("SUB", StringComparer.OrdinalIgnoreCase);
        result.has_both = result.member_methods.Contains("BOTH", StringComparer.OrdinalIgnoreCase);

        if (result.has_sub && result.has_nvl)
            result.case_label = "SUB-NVL";
        else if (result.has_sub && !result.has_nvl && !result.has_both)
            result.case_label = "SUB-SUB";
        else if (result.has_nvl && !result.has_sub && !result.has_both)
            result.case_label = "NVL-NVL";
        else if (result.has_both)
            result.case_label = "HAS-BOTH";
        else
            result.case_label = "UNKNOWN";

        return result;
    }

    private static decimal ResolveMainReferenceQtyForPrepare(
        IReadOnlyList<TaskReferenceInputDto>? refs)
    {
        if (refs == null || refs.Count == 0)
            return 0m;

        var candidates = refs
            .Where(IsMainQtyReferenceForQrPrepare)
            .ToList();

        if (candidates.Count == 0)
            return 0m;

        /*
         * Ưu tiên actual_qty_prev_stage.
         * Nếu actual chưa có thì fallback estimated_qty.
         */
        var actualTotal = candidates
            .Where(x => x.actual_qty_prev_stage > 0)
            .Sum(x => x.actual_qty_prev_stage);

        if (actualTotal > 0)
            return actualTotal;

        return candidates
            .Where(x => x.estimated_qty > 0)
            .Sum(x => x.estimated_qty);
    }

    private static List<TaskReferenceInputDto> NormalizeReferenceInputsForQrPrepare(
        IReadOnlyList<TaskReferenceInputDto>? refs,
        int effectiveQty,
        bool forceActualTotal)
    {
        var result = (refs ?? Array.Empty<TaskReferenceInputDto>())
            .Select(x => new TaskReferenceInputDto
            {
                input_code = x.input_code,
                input_name = x.input_name,
                unit = string.IsNullOrWhiteSpace(x.unit) ? "sp" : x.unit,

                /*
                 * Không phá estimated_qty cũ.
                 */
                estimated_qty = x.estimated_qty,

                actual_qty_prev_stage = x.actual_qty_prev_stage
            })
            .ToList();

        if (!forceActualTotal || effectiveQty <= 0)
            return result;

        var mainRefs = result
            .Where(IsMainQtyReferenceForQrPrepare)
            .ToList();

        if (mainRefs.Count == 0)
            return result;

        /*
         * Nếu chỉ có 1 reference chính, set actual thẳng bằng effectiveQty.
         * Đây là case thường gặp: PHU -> CAN, CAN -> BOI, IN -> PHU...
         */
        if (mainRefs.Count == 1)
        {
            var item = mainRefs[0];

            item.actual_qty_prev_stage = effectiveQty;

            if (item.estimated_qty <= 0)
                item.estimated_qty = effectiveQty;

            return result;
        }

        /*
         * Nếu có nhiều reference chính, phân bổ theo tỷ lệ actual/estimated hiện có.
         */
        var totalWeight = mainRefs.Sum(x =>
            x.actual_qty_prev_stage > 0
                ? x.actual_qty_prev_stage
                : x.estimated_qty > 0
                    ? x.estimated_qty
                    : 1m);

        if (totalWeight <= 0)
            totalWeight = mainRefs.Count;

        decimal remaining = effectiveQty;

        for (var i = 0; i < mainRefs.Count; i++)
        {
            var item = mainRefs[i];

            decimal value;

            if (i == mainRefs.Count - 1)
            {
                value = remaining;
            }
            else
            {
                var weight =
                    item.actual_qty_prev_stage > 0
                        ? item.actual_qty_prev_stage
                        : item.estimated_qty > 0
                            ? item.estimated_qty
                            : 1m;

                value = Math.Round(
                    effectiveQty * weight / totalWeight,
                    4,
                    MidpointRounding.AwayFromZero);

                if (value > remaining)
                    value = remaining;
            }

            item.actual_qty_prev_stage = value;

            if (item.estimated_qty <= 0)
                item.estimated_qty = value;

            remaining -= value;
        }

        return result;
    }

    private static bool CanQrPrepareUseReferenceQty(
        string? processCode,
        IReadOnlyList<TaskReferenceInputDto>? refs)
    {
        var code = NormQrProcessCode(processCode);

        if (string.IsNullOrWhiteSpace(code))
            return false;

        /*
         * RALO không có BTP input.
         * CAT thường là cắt giấy, giữ policy cũ.
         */
        if (code is "RALO" or "CAT")
            return false;

        if (refs == null || refs.Count == 0)
            return false;

        return refs.Any(IsMainQtyReferenceForQrPrepare);
    }

    private static bool IsMainQtyReferenceForQrPrepare(TaskReferenceInputDto? input)
    {
        if (input == null)
            return false;

        var code = NormQrProcessCode(input.input_code);
        var unit = (input.unit ?? "").Trim().ToLowerInvariant();

        /*
         * Không cộng bản kẽm vào số lượng sản phẩm.
         * IN có thể có:
         * - CAT: giấy đã cắt
         * - PLATE_FROM_RALO: bản kẽm in
         */
        if (code == "PLATE_FROM_RALO")
            return false;

        if (unit is "bản" or "ban" or "plate")
            return false;

        if (input.actual_qty_prev_stage <= 0 && input.estimated_qty <= 0)
            return false;

        return true;
    }

    private static string ResolveQrUnit(
        string? processCode,
        IReadOnlyList<TaskReferenceInputDto>? refs,
        string? fallback)
    {
        var mainRef = refs?
            .FirstOrDefault(IsMainQtyReferenceForQrPrepare);

        if (!string.IsNullOrWhiteSpace(mainRef?.unit))
            return mainRef!.unit!;

        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback!;

        var code = NormQrProcessCode(processCode);

        return code switch
        {
            "RALO" => "bản",
            "CAT" => "tờ",
            "IN" => "tờ",
            "BOI" => "tờ",
            _ => "sp"
        };
    }

    private static string NormQrMethod(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToUpperInvariant();
    }

    [HttpPost("qr")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<ActionResult<CreateTaskQrCompactResponse>> CreateQr(
    [FromForm] CreateTaskQrFormRequest form,
    CancellationToken ct)
    {
        if (form == null)
        {
            return BadRequest(new
            {
                message = "Request body is required."
            });
        }

        if (form.task_id <= 0)
        {
            return BadRequest(new
            {
                message = "task_id không hợp lệ.",
                task_id = form.task_id
            });
        }

        List<TaskMaterialUsageInputDto> formMaterials;
        List<TaskReferenceUsageInputDto> formRefs;
        List<TaskOutputReportDto> formOutputs;
        List<string> imageUrls;
        try
        {

            await NormalizeCreateQrFormJsonFieldsAsync(
                form,
                ct);

            formMaterials = ParseJsonList<TaskMaterialUsageInputDto>(
                form.materials_json);

            formRefs = ParseJsonList<TaskReferenceUsageInputDto>(
                form.reference_inputs_json);

            formOutputs = ParseJsonList<TaskOutputReportDto>(
                form.outputs_json);

            imageUrls = await UploadTaskReportImagesAsync(
                form.images,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                task_id = form.task_id
            });
        }

        var reportImageUrl = imageUrls.Count == 0
            ? null
            : string.Join(",", imageUrls);

        var req = new CreateTaskQrRequest
        {
            task_id = form.task_id,
            ttl_minutes = form.ttl_minutes,
            qty_good = form.qty_good,
            use_manual_input = form.use_manual_input,
            materials = formMaterials
        };

        var reason = string.IsNullOrWhiteSpace(form.reason)
            ? null
            : form.reason.Trim();

        var t = await _taskRepo.GetByIdAsync(req.task_id);

        if (t == null)
        {
            return NotFound(new
            {
                message = "Task not found",
                task_id = req.task_id
            });
        }

        var dep = await ProductionDependencyValidator.CheckTaskCanStartAsync(
            _db,
            req.task_id,
            ct);

        if (!dep.can_start)
        {
            var currentTaskMeta = await _db.tasks
                .AsNoTracking()
                .Include(x => x.process)
                .FirstOrDefaultAsync(x => x.task_id == req.task_id, ct);

            return BadRequest(new
            {
                message = "Chưa thể tạo QR vì công đoạn trước đó chưa hoàn thành.",
                task_id = req.task_id,
                prod_id = currentTaskMeta?.prod_id,
                process_code = currentTaskMeta?.process?.process_code,
                detail = dep.message,
                issues = dep.issues
            });
        }

        var taskMeta = await _db.tasks
            .AsNoTracking()
            .Include(x => x.prod)
            .Include(x => x.process)
            .FirstOrDefaultAsync(x => x.task_id == req.task_id, ct);

        if (taskMeta == null)
        {
            return NotFound(new
            {
                message = "Task not found",
                task_id = req.task_id
            });
        }

        List<TaskReferenceUsageInputDto> qrReferenceInputs;

        try
        {
            /*
             * qrReferenceInputs là dữ liệu dùng thật khi finish:
             * reference_inputs_json gốc + sub_product_leftovers_json đã merge.
             */
            qrReferenceInputs = BuildQrReferenceInputsWithSubProductLeftovers(
                formRefs,
                taskMeta.process?.process_code,
                taskMeta.process?.process_name);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                task_id = req.task_id
            });
        }

        var isGroupTask =
            taskMeta.prod != null &&
            string.Equals(
                taskMeta.prod.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

        var isManualTask =
            string.Equals(
                taskMeta.input_mode,
                "MANUAL",
                StringComparison.OrdinalIgnoreCase);

        var ttlMinutes = req.ttl_minutes <= 0
            ? 10
            : req.ttl_minutes;

        var ttl = TimeSpan.FromMinutes(ttlMinutes);

        /*
         * JSON này chứa full dữ liệu FE nhập vào request.
         * Sẽ được nhét vào token.
         */
        var submittedJson = BuildSubmittedJsonForToken(
            form,
            formMaterials,
            formRefs,
            qrReferenceInputs,
            formOutputs,
            imageUrls,
            reason,
            reportImageUrl);

        /*
         * CASE 1: GROUP TASK
         */
        if (isGroupTask)
        {
            var preparedBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
                req.task_id,
                ct);

            var groupPolicy = await ResolveGroupQrQtyPolicyAsync(
                taskMeta,
                preparedReferenceInputs: preparedBundle.reference_inputs,
                submittedReferenceInputs: qrReferenceInputs,
                ct: ct);

            var maxAllowed = groupPolicy.MaxAllowed;
            var suggestedQty = groupPolicy.SuggestedQty;

            if (maxAllowed <= 0)
            {
                return BadRequest(new
                {
                    message = "Production ghép chưa có số lượng hợp lệ.",
                    task_id = req.task_id,
                    prod_id = taskMeta.prod_id
                });
            }

            var isAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

            var qtyGood = isAuto
                ? suggestedQty
                : req.qty_good!.Value;

            if (qtyGood <= 0)
            {
                return BadRequest(new
                {
                    message = "Số lượng báo cáo phải lớn hơn 0.",
                    task_id = req.task_id,
                    input_qty_good = qtyGood
                });
            }

            if (qtyGood > maxAllowed)
            {
                return BadRequest(new
                {
                    message =
                        $"Số lượng báo cáo group không hợp lệ. " +
                        $"Công đoạn [{taskMeta.process?.process_code} - {taskMeta.process?.process_name}] " +
                        $"chỉ cho phép tối đa {maxAllowed} {groupPolicy.QtyUnit}.",

                    task_id = req.task_id,
                    input_qty_good = qtyGood,
                    max_allowed = maxAllowed,
                    suggested_qty = suggestedQty,
                    qty_unit = groupPolicy.QtyUnit,
                    hint = groupPolicy.Hint
                });
            }

            var token = _tokenSvc.CreateToken(
                req.task_id,
                qtyGood,
                req.materials,
                ttl,
                useManualInput: true,
                reason: reason,
                reportImageUrl: reportImageUrl,
                referenceInputs: qrReferenceInputs,
                outputs: formOutputs);

            return Ok(BuildCompactQrResponse(
                token,
                req.task_id));
        }

        /*
         * CASE 2: SINGLE MANUAL
         */
        if (req.use_manual_input || isManualTask)
        {
            var manualPolicy = await _taskRepo.GetQtyPolicyAsync(
                req.task_id,
                ct);

            if (manualPolicy == null)
            {
                return BadRequest(new
                {
                    message = "Không xác định được ngưỡng số lượng hợp lệ cho công đoạn này.",
                    task_id = req.task_id
                });
            }

            var isAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

            int qtyGood;

            if (isAuto)
            {
                qtyGood = manualPolicy.suggested_qty;

                if (qtyGood <= 0)
                    qtyGood = 1;
            }
            else
            {
                qtyGood = req.qty_good!.Value;

                if (qtyGood < manualPolicy.min_allowed ||
                    qtyGood > manualPolicy.max_allowed)
                {
                    return BadRequest(new
                    {
                        message =
                            $"Số lượng báo cáo không hợp lệ. " +
                            $"Công đoạn [{manualPolicy.process_code} - {manualPolicy.process_name}] " +
                            $"chỉ cho phép trong khoảng {manualPolicy.min_allowed} -> {manualPolicy.max_allowed} {manualPolicy.qty_unit}.",

                        task_id = req.task_id,
                        input_qty_good = qtyGood,
                        min_allowed = manualPolicy.min_allowed,
                        max_allowed = manualPolicy.max_allowed,
                        suggested_qty = manualPolicy.suggested_qty,
                        qty_unit = manualPolicy.qty_unit
                    });
                }
            }

            var token = _tokenSvc.CreateToken(
                req.task_id,
                qtyGood,
                req.materials,
                ttl,
                useManualInput: true,
                reason: reason,
                reportImageUrl: reportImageUrl,
                referenceInputs: qrReferenceInputs,
                outputs: formOutputs);

            return Ok(BuildCompactQrResponse(
                token,
                req.task_id));
        }

        /*
         * CASE 3: SINGLE ESTIMATE FLOW CŨ
         */
        var policy = await _taskRepo.GetQtyPolicyAsync(
            req.task_id,
            ct);

        if (policy == null)
        {
            return BadRequest(new
            {
                message = "Không xác định được ngưỡng số lượng hợp lệ cho công đoạn này.",
                task_id = req.task_id
            });
        }

        var oldFlowIsAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

        int oldFlowQtyGood;

        if (oldFlowIsAuto)
        {
            oldFlowQtyGood = policy.suggested_qty;

            if (oldFlowQtyGood <= 0)
                oldFlowQtyGood = 1;
        }
        else
        {
            oldFlowQtyGood = req.qty_good!.Value;

            if (oldFlowQtyGood < policy.min_allowed ||
                oldFlowQtyGood > policy.max_allowed)
            {
                return BadRequest(new
                {
                    message =
                        $"Số lượng báo cáo không hợp lệ. " +
                        $"Công đoạn [{policy.process_code} - {policy.process_name}] " +
                        $"chỉ cho phép trong khoảng {policy.min_allowed} -> {policy.max_allowed} {policy.qty_unit}.",

                    task_id = req.task_id,
                    input_qty_good = oldFlowQtyGood,

                    min_allowed = policy.min_allowed,
                    max_allowed = policy.max_allowed,
                    suggested_qty = policy.suggested_qty,
                    qty_unit = policy.qty_unit
                });
            }
        }

        List<TaskMaterialUsageInputDto> inputMaterials;

        try
        {
            inputMaterials = await _scanSvc.BuildMaterialUsageForQrAsync(
                req.task_id,
                req.materials,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                task_id = req.task_id,

                process_code = policy.process_code,
                process_name = policy.process_name,

                suggested_qty = policy.suggested_qty,
                qty_unit = policy.qty_unit
            });
        }

        /*
         * Lưu ý:
         * - inputMaterials là vật tư thực sự dùng để finish theo flow cũ.
         * - submittedJson vẫn chứa formMaterials FE nhập ban đầu để decode xem lại request.
         */
        var oldFlowToken = _tokenSvc.CreateToken(
            req.task_id,
            oldFlowQtyGood,
            inputMaterials,
            ttl,
            useManualInput: false,
            reason: reason,
            reportImageUrl: reportImageUrl,
            referenceInputs: qrReferenceInputs,
            outputs: formOutputs);



        return Ok(BuildCompactQrResponse(
            oldFlowToken,
            req.task_id));
    }

    [HttpPost("qr/decode")]
    [Consumes("application/json")]
    public async Task<ActionResult<DecodeTaskQrTokenResponse>> DecodeQrToken(
    [FromBody] DecodeTaskQrTokenRequest req,
    CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.token))
        {
            return BadRequest(new
            {
                message = "Token không được rỗng."
            });
        }

        var token = req.token.Trim();

        if (!_tokenSvc.TryValidate(
                token,
                out TaskQrTokenPayloadDto payload,
                out var reason))
        {
            return BadRequest(new
            {
                valid = false,
                message = "Token không hợp lệ hoặc đã hết hạn.",
                reason = reason
            });
        }

        /*
         * Optional: kiểm tra task còn tồn tại để link chắc chắn đúng.
         */
        var exists = await _db.tasks
            .AsNoTracking()
            .AnyAsync(x => x.task_id == payload.task_id, ct);
        var prodId = await _db.tasks
            .AsNoTracking()
            .Where(x => x.task_id == payload.task_id)
            .Select(x => x.prod_id)
            .FirstOrDefaultAsync(ct);

        var taskName = await _db.tasks
            .AsNoTracking()
            .Where(x => x.task_id == payload.task_id)
            .Select(x => x.name)
            .FirstOrDefaultAsync(ct);

        if (!exists)
        {
            return NotFound(new
            {
                valid = false,
                message = "Task trong token không còn tồn tại.",
                task_id = payload.task_id
            });
        }

        return Ok(new DecodeTaskQrTokenResponse
        {
            valid = true,
            token = token,
            link = BuildProductionManagerTaskDetailLink(payload.task_id),
            task_id = payload.task_id,
            task_name = taskName,
            qty_good = payload.qty_good,
            exp_unix = payload.exp_unix,
            use_manual_input = payload.use_manual_input,
            reason = payload.reason,
            report_image_url = payload.report_image_url,
            prod_id = prodId,
            materials = payload.materials ?? new List<TaskMaterialUsageInputDto>(),
            qr_reference_inputs = payload.reference_inputs ?? new List<TaskReferenceUsageInputDto>(),
            outputs = payload.outputs ?? new List<TaskOutputReportDto>(),
        });
    }

    [HttpPost("finish")]
    [Consumes("application/json")]
    public async Task<ActionResult<ScanTaskResult>> Finish(
    [FromBody] FinishTaskTokenRequest req,
    CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.token))
        {
            return BadRequest(new
            {
                message = "Missing token."
            });
        }

        try
        {
            var scanReq = new ScanTaskRequest
            {
                token = req.token.Trim()
            };

            var scannedByUserId = GetCurrentUserId();

            var res = await _scanSvc.ScanFinishAsync(
                scanReq,
                scannedByUserId,
                ct);

            return Ok(res);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private string BuildProductionManagerTaskDetailLink(int taskId)
    {
        /*
         * appsettings hiện có:
         * "Deal": {
         *   "BaseUrlFe": "daiphuchai.vercel.app"
         * }
         */
        var baseUrl =
            _configuration["Deal:BaseUrlFe"] ??
            _configuration["App:BaseUrlFe"] ??
            _configuration["BaseUrlFe"] ??
            "";

        baseUrl = baseUrl.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
            return $"/production-manager/task-detail/{taskId}";

        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "https://" + baseUrl;
        }

        return $"{baseUrl.TrimEnd('/')}/production-manager/task-detail/{taskId}";
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private TaskQrRequestJsonEchoDto BuildRequestJsonEcho(
        CreateTaskQrFormRequest form)
    {
        return new TaskQrRequestJsonEchoDto
        {
            materials_json = NullIfBlank(form.materials_json),
            reference_inputs_json = NullIfBlank(form.reference_inputs_json),
            outputs_json = NullIfBlank(form.outputs_json),
        };
    }

    private TaskQrResponse AttachLinkAndRequestJsonEcho(
        TaskQrResponse response,
        CreateTaskQrFormRequest form)
    {
        response.link = BuildProductionManagerTaskDetailLink(form.task_id);

        response.request_json = BuildRequestJsonEcho(form);

        return response;
    }

    private TaskQrSubmittedPayloadDto BuildSubmittedQrPayload(
    CreateTaskQrFormRequest form,
    List<TaskMaterialUsageInputDto> formMaterials,
    List<TaskReferenceUsageInputDto> formRefs,
    List<TaskReferenceUsageInputDto> qrReferenceInputs,
    List<TaskOutputReportDto> formOutputs,
    List<string> imageUrls,
    string? reason,
    string? reportImageUrl)
    {
        return new TaskQrSubmittedPayloadDto
        {
            task_id = form.task_id,
            ttl_minutes = form.ttl_minutes,
            qty_good = form.qty_good,
            use_manual_input = form.use_manual_input,
            reason = reason,
            report_image_url = reportImageUrl,
            image_urls = imageUrls ?? new List<string>(),

            materials = formMaterials ?? new List<TaskMaterialUsageInputDto>(),
            reference_inputs = formRefs ?? new List<TaskReferenceUsageInputDto>(),
            qr_reference_inputs = qrReferenceInputs ?? new List<TaskReferenceUsageInputDto>(),
            outputs = formOutputs ?? new List<TaskOutputReportDto>(),

            raw_json = new TaskQrSubmittedRawJsonDto
            {
                materials_json = NullIfBlank(form.materials_json),
                reference_inputs_json = NullIfBlank(form.reference_inputs_json),
                outputs_json = NullIfBlank(form.outputs_json),
            }
        };
    }

    private TaskQrResponse AttachSubmittedPayloadToQrResponse(
    TaskQrResponse response,
    CreateTaskQrFormRequest form,
    List<TaskMaterialUsageInputDto> formMaterials,
    List<TaskReferenceUsageInputDto> formRefs,
    List<TaskReferenceUsageInputDto> qrReferenceInputs,
    List<TaskOutputReportDto> formOutputs,
    List<TaskSubProductLeftoverInputDto> formSubProductLeftovers,
    List<string> imageUrls,
    string? reason,
    string? reportImageUrl)
    {
        /*
         * Giữ link như yêu cầu cũ.
         */
        response.link = BuildProductionManagerTaskDetailLink(form.task_id);

        /*
         * NEW:
         * Trả raw JSON FE đã nhập ở request.
         * Cái nào không nhập thì null.
         */
        response.request_json = BuildRequestJsonEcho(form);

        /*
         * Giữ submitted_payload nếu bạn vẫn muốn response cũ.
         */
        response.submitted_payload = BuildSubmittedQrPayload(
            form,
            formMaterials,
            formRefs,
            qrReferenceInputs,
            formOutputs,
            imageUrls,
            reason,
            reportImageUrl);

        return response;
    }

    private async Task<List<string>> UploadTaskReportImagesAsync(
    List<IFormFile>? images,
    CancellationToken ct)
    {
        var files = (images ?? new List<IFormFile>())
            .Where(x => x != null && x.Length > 0)
            .ToList();

        var urls = new List<string>();

        if (files.Count == 0)
            return urls;

        const int maxImageCount = 10;
        const long maxEachImageBytes = 5 * 1024 * 1024;
        const long maxTotalImageBytes = 30 * 1024 * 1024;

        if (files.Count > maxImageCount)
        {
            throw new InvalidOperationException(
                $"Chỉ được upload tối đa {maxImageCount} ảnh cho một lần báo cáo.");
        }

        var totalBytes = files.Sum(x => x.Length);
        if (totalBytes > maxTotalImageBytes)
        {
            throw new InvalidOperationException(
                "Tổng dung lượng ảnh upload không được vượt quá 30MB.");
        }

        foreach (var image in files)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(image.ContentType) ||
                !image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"File {image.FileName} không phải là ảnh.");
            }

            if (image.Length > maxEachImageBytes)
            {
                throw new InvalidOperationException(
                    $"Ảnh {image.FileName} không được vượt quá 5MB.");
            }

            await using var stream = image.OpenReadStream();

            var url = await _fileStorage.UploadAsync(
                stream,
                image.FileName,
                image.ContentType,
                "task-reports");

            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url.Trim());
        }

        return urls;
    }

    [HttpPut("ready")]
    public async Task<IActionResult> SetTaskReady(
        [FromBody] SetTaskReadyRequest req,
        CancellationToken ct)
    {
        if (req == null || req.task_id <= 0)
        {
            return BadRequest(new
            {
                message = "task_id không hợp lệ."
            });
        }

        try
        {
            var ok = await _taskService.SetTaskReadyAsync(req.task_id, ct);

            if (!ok)
            {
                return NotFound(new
                {
                    message = "Task not found",
                    task_id = req.task_id
                });
            }

            await _hub.Clients.All.SendAsync(
                "update-ui",
                new { message = "Update UI" },
                ct);

            return Ok(new
            {
                message = "Task status updated to Ready",
                task_id = req.task_id,
                status = "Ready"
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                task_id = req.task_id
            });
        }
    }

    [HttpPost("cancel-finish/{taskId:int}")]
    public async Task<IActionResult> CancelFinish(
        int taskId,
        [FromBody] CancelTaskFinishRequest? req,
        CancellationToken ct)
    {
        if (taskId <= 0)
        {
            return BadRequest(new
            {
                message = "task_id không hợp lệ.",
                task_id = taskId
            });
        }

        try
        {
            var cancelledByUserId = GetCurrentUserId();

            var result = await _scanSvc.CancelTaskFinishAsync(
                taskId,
                req,
                cancelledByUserId,
                ct);

            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new
            {
                message = ex.Message,
                task_id = taskId
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                task_id = taskId
            });
        }
    }

    [HttpPost("finish-from-stock")]
    public async Task<IActionResult> FinishFromStock(
        [FromBody] FinishTasksFromStockRequest req,
        CancellationToken ct)
    {
        if (req == null || req.task_ids == null || req.task_ids.Count == 0)
        {
            return BadRequest(new
            {
                message = "task_ids is required"
            });
        }

        var taskIds = req.task_ids
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (taskIds.Count == 0)
        {
            return BadRequest(new
            {
                message = "task_ids must contain valid positive integers"
            });
        }

        try
        {
            var result = await _taskService.FinishTasksFromStockAsync(
                taskIds,
                GetCurrentUserId(),
                ct);

            if (result.not_found_task_ids.Any())
            {
                return NotFound(new
                {
                    message = "Some tasks were not found",
                    not_found_task_ids = result.not_found_task_ids,
                    finished_task_ids = result.finished_task_ids,
                    already_finished_task_ids = result.already_finished_task_ids
                });
            }

            return Ok(new
            {
                message = "Tasks status updated to Finished",
                finished_task_ids = result.finished_task_ids,
                already_finished_task_ids = result.already_finished_task_ids,
                status = "Finished",
                reason = "Bán thành phẩm đã có sẵn trong kho",
                is_taken_sub_product = true
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message,
                task_ids = taskIds
            });
        }
    }

    [HttpGet("get-all-task")]
    public async Task<IActionResult> GetAllTask()
    {
        var task = await _db.tasks.ToListAsync();
        return Ok(task);
    }

    private static List<T> ParseJsonList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<T>();

        try
        {
            return JsonSerializer.Deserialize<List<T>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<T>();
        }
        catch
        {
            throw new InvalidOperationException("JSON input không hợp lệ.");
        }
    }

    private static List<TaskReferenceUsageInputDto> BuildQrReferenceInputsWithSubProductLeftovers(
    List<TaskReferenceUsageInputDto>? referenceInputs,
    string? currentProcessCode,
    string? currentProcessName)
    {
        var result = new List<TaskReferenceUsageInputDto>();

        if (referenceInputs != null && referenceInputs.Count > 0)
        {
            result.AddRange(referenceInputs
                .Where(x => !string.IsNullOrWhiteSpace(x.input_code))
                .Select(x => new TaskReferenceUsageInputDto
                {
                    input_code = x.input_code.Trim(),
                    input_name = string.IsNullOrWhiteSpace(x.input_name) ? null : x.input_name.Trim(),
                    unit = string.IsNullOrWhiteSpace(x.unit) ? null : x.unit.Trim(),
                    quantity_used = Math.Round(x.quantity_used, 4),
                    quantity_left = Math.Round(x.quantity_left, 4)
                }));
        }

        var fallbackCode = NormQrProcessCode(currentProcessCode);

        return result
    .Where(x => !string.IsNullOrWhiteSpace(x.input_code))
    .GroupBy(x => new
    {
        input_code = NormQrProcessCode(x.input_code),
        unit = (x.unit ?? "sp").Trim().ToLowerInvariant()
    })
    .Select(g =>
    {
        var first = g.First();

        return new TaskReferenceUsageInputDto
        {
            input_code = g.Key.input_code,
            input_name = first.input_name,
            unit = first.unit,
            quantity_used = Math.Round(g.Sum(x => x.quantity_used), 4),
            quantity_left = Math.Round(g.Sum(x => x.quantity_left), 4)
        };
    })
    .ToList();
    }

    private static string NormQrProcessCode(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }

    private static bool CanImportAsSubProductStage(string? processCode)
    {
        var code = NormQrProcessCode(processCode);

        return code is
            "RALO" or
            "CAT" or
            "IN" or
            "PHU" or
            "CAN" or
            "CAN_MANG" or
            "BOI" or
            "BE" or
            "DUT";
    }

    private sealed class GroupQrQtyPolicy
    {
        public bool IsGroupSub { get; set; }

        public int MinAllowed { get; set; } = 1;

        public int MaxAllowed { get; set; }
        public int SuggestedQty { get; set; }
        public int HappyCaseQty { get; set; }

        public string QtyUnit { get; set; } = "sp";
        public string Hint { get; set; } = "";

        public List<TaskReferenceInputDto> ReferenceInputs { get; set; } = new();
    }
    private static bool IsGroupSubTask(task taskMeta)
    {
        return taskMeta?.prod != null
               && string.Equals(taskMeta.prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase)
               && string.Equals(taskMeta.prod.prod_method, "SUB", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ResolveGroupSubPlannedInputQty(
        IReadOnlyList<TaskReferenceInputDto>? referenceInputs)
    {
        if (referenceInputs == null || referenceInputs.Count == 0)
            return 0m;

        /*
         * GROUP + SUB:
         * Số chuẩn là estimated_qty, ví dụ 6290.
         * Không lấy actual_qty_prev_stage nếu actual đang bị kéo từ log IN cũ.
         */
        var estimated = referenceInputs
            .Where(x => x != null)
            .Sum(x => x.estimated_qty);

        if (estimated > 0)
            return estimated;

        var actual = referenceInputs
            .Where(x => x != null)
            .Sum(x => x.actual_qty_prev_stage);

        return actual > 0 ? actual : 0m;
    }

    private static List<TaskReferenceInputDto> NormalizeGroupSubReferenceInputs(
        IReadOnlyList<TaskReferenceInputDto>? referenceInputs,
        int plannedInputQty)
    {
        var result = (referenceInputs ?? Array.Empty<TaskReferenceInputDto>())
            .Select(x => new TaskReferenceInputDto
            {
                input_code = x.input_code,
                input_name = x.input_name,
                unit = string.IsNullOrWhiteSpace(x.unit) ? "sp" : x.unit,

                /*
                 * Đồng bộ cả estimated và actual về plannedInputQty.
                 */
                estimated_qty = plannedInputQty,
                actual_qty_prev_stage = plannedInputQty
            })
            .ToList();

        if (result.Count == 0)
        {
            result.Add(new TaskReferenceInputDto
            {
                input_code = "SUB_BTP_INPUT",
                input_name = "Bán thành phẩm đầu vào từ SUB",
                unit = "sp",
                estimated_qty = plannedInputQty,
                actual_qty_prev_stage = plannedInputQty
            });
        }

        return result;
    }

    private static string NormQrCode(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }

    private static decimal ResolveActualQtyFromPreparedReferenceInputs(
        IReadOnlyList<TaskReferenceInputDto>? referenceInputs)
    {
        if (referenceInputs == null || referenceInputs.Count == 0)
            return 0m;

        /*
         * Với GROUP + SUB, input BTP thực tế nằm ở actual_qty_prev_stage.
         * Ví dụ:
         * reference_inputs[0].actual_qty_prev_stage = 7674
         */
        return referenceInputs
            .Where(x => x != null)
            .Sum(x => x.actual_qty_prev_stage);
    }

    private static decimal ResolveActualQtyFromSubmittedReferenceInputs(
        List<TaskReferenceUsageInputDto>? referenceInputs)
    {
        if (referenceInputs == null || referenceInputs.Count == 0)
            return 0m;

        return referenceInputs
            .Where(x => x != null)
            .Sum(x =>
            {
                var used = x.quantity_used > 0 ? x.quantity_used : 0m;
                var left = x.quantity_left > 0 ? x.quantity_left : 0m;

                return used + left;
            });
    }

    private async Task<GroupQrQtyPolicy> ResolveGroupQrQtyPolicyAsync(
    task taskMeta,
    IReadOnlyList<TaskReferenceInputDto>? preparedReferenceInputs,
    List<TaskReferenceUsageInputDto>? submittedReferenceInputs,
    CancellationToken ct)
    {
        if (taskMeta?.prod == null)
        {
            taskMeta!.prod = await _db.productions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.prod_id == taskMeta.prod_id, ct);
        }

        if (taskMeta?.prod == null)
        {
            return new GroupQrQtyPolicy
            {
                IsGroupSub = false,
                MinAllowed = 1,
                MaxAllowed = 1,
                SuggestedQty = 1,
                HappyCaseQty = 1,
                QtyUnit = "sp",
                Hint = "Không tìm thấy production của task.",
                ReferenceInputs = new List<TaskReferenceInputDto>()
            };
        }

        /*
         * Dùng lại unified policy để POST /qr và GET /qr-prepare cùng số.
         */
        var unified = await ResolveGroupQrPrepareUnifiedPolicyAsync(
            taskMeta,
            ct);

        /*
         * Nếu FE submit reference_inputs_json khi tạo QR,
         * ưu tiên số FE submit để không lệch token.
         */
        var submittedQty = ResolveSubmittedReferenceQtyForQr(
            submittedReferenceInputs);

        if (submittedQty > 0)
        {
            var submittedEffectiveQty = CeilPositive(submittedQty);

            unified.max_allowed = Math.Max(unified.max_allowed, submittedEffectiveQty);
            unified.suggested_qty = submittedEffectiveQty;
            unified.happy_case_qty = submittedEffectiveQty;
            unified.production_output_qty = submittedEffectiveQty;

            unified.reference_inputs = NormalizeReferenceInputsForQrPrepare(
                unified.reference_inputs,
                submittedEffectiveQty,
                forceActualTotal: true);

            unified.manual_input_hint +=
                $" FE submitted reference_qty={Math.Round(submittedQty, 4)} nên QR token dùng suggested_qty={submittedEffectiveQty}.";
        }

        return new GroupQrQtyPolicy
        {
            IsGroupSub = true,
            MinAllowed = unified.min_allowed,
            MaxAllowed = unified.max_allowed,
            SuggestedQty = unified.suggested_qty,
            HappyCaseQty = unified.happy_case_qty,
            QtyUnit = unified.qty_unit,
            Hint = unified.manual_input_hint,
            ReferenceInputs = unified.reference_inputs
        };
    }

    private static decimal ResolveSubmittedReferenceQtyForQr(
        IReadOnlyList<TaskReferenceUsageInputDto>? refs)
    {
        if (refs == null || refs.Count == 0)
            return 0m;

        return refs
            .Where(x => x != null)
            .Where(x => IsMainQtyReferenceForSubmittedQr(x.input_code, x.unit))
            .Sum(x =>
            {
                var used = x.quantity_used > 0 ? x.quantity_used : 0m;
                var left = x.quantity_left > 0 ? x.quantity_left : 0m;

                return used + left;
            });
    }

    private static bool IsMainQtyReferenceForSubmittedQr(
        string? inputCode,
        string? unit)
    {
        var code = NormQrProcessCode(inputCode);
        var u = (unit ?? "").Trim().ToLowerInvariant();

        if (code == "PLATE_FROM_RALO")
            return false;

        if (u is "bản" or "ban" or "plate")
            return false;

        return true;
    }

    private static decimal ResolveGroupPreparedQtyForQrPolicy(
    IReadOnlyList<TaskReferenceInputDto>? referenceInputs)
    {
        if (referenceInputs == null || referenceInputs.Count == 0)
            return 0m;

        var total = referenceInputs
            .Where(x => x != null)
            .Sum(x =>
            {
                var estimated = x.estimated_qty > 0 ? x.estimated_qty : 0m;
                var actual = x.actual_qty_prev_stage > 0 ? x.actual_qty_prev_stage : 0m;

                return Math.Max(estimated, actual);
            });

        return total > 0 ? total : 0m;
    }

    private static List<TaskReferenceInputDto> NormalizeGroupReferenceInputsForQrPolicy(
        IReadOnlyList<TaskReferenceInputDto>? referenceInputs,
        int effectiveQty)
    {
        var refs = (referenceInputs ?? Array.Empty<TaskReferenceInputDto>())
            .Where(x => x != null)
            .Select(x =>
            {
                var estimated = x.estimated_qty > 0
                    ? x.estimated_qty
                    : x.actual_qty_prev_stage;

                var actual = x.actual_qty_prev_stage > 0
                    ? x.actual_qty_prev_stage
                    : estimated;

                return new TaskReferenceInputDto
                {
                    input_code = string.IsNullOrWhiteSpace(x.input_code)
                        ? "REFERENCE_INPUT"
                        : x.input_code,

                    input_name = string.IsNullOrWhiteSpace(x.input_name)
                        ? "Bán thành phẩm đầu vào"
                        : x.input_name,

                    unit = string.IsNullOrWhiteSpace(x.unit)
                        ? "sp"
                        : x.unit,

                    estimated_qty = Math.Round(estimated, 4, MidpointRounding.AwayFromZero),
                    actual_qty_prev_stage = Math.Round(actual, 4, MidpointRounding.AwayFromZero)
                };
            })
            .ToList();

        if (refs.Count > 0)
            return refs;

        return new List<TaskReferenceInputDto>
    {
        new TaskReferenceInputDto
        {
            input_code = "REFERENCE_INPUT",
            input_name = "Bán thành phẩm đầu vào",
            unit = "sp",
            estimated_qty = effectiveQty,
            actual_qty_prev_stage = effectiveQty
        }
    };
    }

    private static int CeilPositive(decimal value)
    {
        return value <= 0
            ? 0
            : (int)Math.Ceiling(value);
    }

    private static readonly JsonSerializerOptions QrSubmittedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private string BuildSubmittedJsonForToken(
        CreateTaskQrFormRequest form,
        List<TaskMaterialUsageInputDto> formMaterials,
        List<TaskReferenceUsageInputDto> formRefs,
        List<TaskReferenceUsageInputDto> qrReferenceInputs,
        List<TaskOutputReportDto> formOutputs,
        List<string> imageUrls,
        string? reason,
        string? reportImageUrl)
    {
        var payload = BuildSubmittedQrPayload(
            form,
            formMaterials,
            formRefs,
            qrReferenceInputs,
            formOutputs,
            imageUrls,
            reason,
            reportImageUrl);

        return JsonSerializer.Serialize(
            payload,
            QrSubmittedJsonOptions);
    }

    private static TaskQrSubmittedPayloadDto? TryDeserializeSubmittedPayloadFromToken(
        string? submittedJson)
    {
        if (string.IsNullOrWhiteSpace(submittedJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TaskQrSubmittedPayloadDto>(
                submittedJson,
                QrSubmittedJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private CreateTaskQrCompactResponse BuildCompactQrResponse(
        string token,
        int taskId)
    {
        return new CreateTaskQrCompactResponse
        {
            token = token,
            link = BuildProductionManagerTaskDetailLink(taskId)
        };
    }

    private static readonly JsonSerializerOptions QrFormJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private async Task NormalizeCreateQrFormJsonFieldsAsync(
        CreateTaskQrFormRequest form,
        CancellationToken ct)
    {
        if (form == null)
            return;

        if (!Request.HasFormContentType)
            return;

        var rawForm = await Request.ReadFormAsync(ct);

        /*
         * 1. materials_json
         */
        if (string.IsNullOrWhiteSpace(form.materials_json))
        {
            form.materials_json = BuildMaterialsJsonFromFlatForm(rawForm);
        }

        /*
         * 2. reference_inputs_json
         */
        if (string.IsNullOrWhiteSpace(form.reference_inputs_json))
        {
            form.reference_inputs_json = BuildReferenceInputsJsonFromFlatForm(rawForm);
        }

        /*
         * 3. outputs_json
         */
        if (string.IsNullOrWhiteSpace(form.outputs_json))
        {
            form.outputs_json = BuildOutputsJsonFromFlatForm(rawForm);
        }
    }

    private static string? BuildMaterialsJsonFromFlatForm(IFormCollection form)
    {
        /*
         * Nếu không có material_id thì không build material.
         */
        var materialIds = GetFormValues(
            form,
            "material_id",
            "materialId",
            "materials[0].material_id");

        if (materialIds.Count == 0)
            return null;

        var materialCodes = GetFormValues(
            form,
            "material_code",
            "materialCode");

        var materialNames = GetFormValues(
            form,
            "material_name",
            "materialName");

        var units = GetFormValues(
            form,
            "material_unit",
            "materialUnit",
            "unit");

        /*
         * NEW:
         * Ưu tiên mat_quantity_used / mat_quantity_left để phân biệt với BTP/reference input.
         *
         * OLD:
         * Vẫn nhận material_quantity_used, quantity_used để không vỡ FE/API cũ.
         */
        var quantityUsedValues = GetFormValues(
            form,
            "mat_quantity_used",
            "matQuantityUsed",
            "material_quantity_used",
            "materialQuantityUsed",
            "quantity_used",
            "quantityUsed");

        var quantityLeftValues = GetFormValues(
            form,
            "mat_quantity_left",
            "matQuantityLeft",
            "material_quantity_left",
            "materialQuantityLeft",
            "quantity_left",
            "quantityLeft");

        var isStockValues = GetFormValues(
            form,
            "material_is_stock",
            "materialIsStock",
            "is_stock",
            "isStock");

        var result = new List<TaskMaterialUsageInputDto>();

        for (var i = 0; i < materialIds.Count; i++)
        {
            var materialId = ReadIntAt(materialIds, i);

            if (materialId <= 0)
                continue;

            /*
             * Nếu FE gửi đúng field mới mat_quantity_used/mat_quantity_left,
             * lấy theo index bình thường.
             *
             * Nếu FE còn dùng quantity_used chung bị lặp với reference input,
             * giữ fallback cũ: lấy phần tử sau để tránh lấy nhầm BTP.
             */
            var hasDedicatedMaterialUsedKey = HasAnyKey(
                form,
                "mat_quantity_used",
                "matQuantityUsed",
                "material_quantity_used",
                "materialQuantityUsed");

            var hasDedicatedMaterialLeftKey = HasAnyKey(
                form,
                "mat_quantity_left",
                "matQuantityLeft",
                "material_quantity_left",
                "materialQuantityLeft");

            var used = hasDedicatedMaterialUsedKey
                ? ReadDecimalAt(quantityUsedValues, i)
                : ReadDecimalAt(quantityUsedValues, Math.Min(i + 1, quantityUsedValues.Count - 1));

            var left = hasDedicatedMaterialLeftKey
                ? ReadDecimalAt(quantityLeftValues, i)
                : ReadDecimalAt(quantityLeftValues, Math.Min(i + 1, quantityLeftValues.Count - 1));

            result.Add(new TaskMaterialUsageInputDto
            {
                material_id = materialId,
                material_code = ReadStringAt(materialCodes, i),
                material_name = ReadStringAt(materialNames, i),
                unit = ReadStringAt(units, i) ?? "kg",

                quantity_used = Math.Round(used, 4),
                quantity_left = Math.Round(left, 4),

                is_stock = ReadBoolAt(isStockValues, i, fallback: false)
            });
        }

        return result.Count == 0
            ? null
            : JsonSerializer.Serialize(result, QrFormJsonOptions);
    }

    private static string? BuildReferenceInputsJsonFromFlatForm(IFormCollection form)
    {
        var inputCode = FirstFormValue(form, "input_code", "inputCode", "reference_input_code", "referenceInputCode");

        if (string.IsNullOrWhiteSpace(inputCode))
            return null;

        var quantityUsedValues = GetFormValues(form, "reference_quantity_used", "referenceQuantityUsed", "input_quantity_used", "inputQuantityUsed", "quantity_used", "quantityUsed");
        var quantityLeftValues = GetFormValues(form, "reference_quantity_left", "referenceQuantityLeft", "input_quantity_left", "inputQuantityLeft", "quantity_left", "quantityLeft");

        /*
         * Với curl của bạn:
         * quantity_used lặp 2 lần:
         * - lần 1 thường là BTP/reference input = 6289
         * - lần 2 thường là material = 38.2076
         *
         * Nên reference input lấy phần tử đầu.
         */
        var quantityUsed = ReadDecimalAt(quantityUsedValues, 0);
        var quantityLeft = ReadDecimalAt(quantityLeftValues, 0);

        var result = new List<TaskReferenceUsageInputDto>
    {
        new TaskReferenceUsageInputDto
        {
            input_code = inputCode.Trim(),
            input_name = FirstFormValue(form, "input_name", "inputName", "reference_input_name", "referenceInputName"),
            unit = FirstFormValue(form, "input_unit", "reference_unit", "unit") ?? "sp",
            quantity_used = Math.Round(quantityUsed, 4),
            quantity_left = Math.Round(quantityLeft, 4)
        }
    };

        return JsonSerializer.Serialize(result, QrFormJsonOptions);
    }

    private static string? BuildOutputsJsonFromFlatForm(IFormCollection form)
    {
        var outputCode = FirstFormValue(form, "output_code", "outputCode");

        if (string.IsNullOrWhiteSpace(outputCode))
            return null;

        var quantityGood = ReadDecimalAt(
            GetFormValues(form, "output_quantity_good", "outputQuantityGood", "quantity_good", "quantityGood"),
            0);

        var quantityBad = ReadDecimalAt(
            GetFormValues(form, "output_quantity_bad", "outputQuantityBad", "quantity_bad", "quantityBad"),
            0);

        var result = new List<TaskOutputReportDto>
    {
        new TaskOutputReportDto
        {
            output_code = outputCode.Trim(),
            output_name = FirstFormValue(form, "output_name", "outputName"),
            unit = FirstFormValue(form, "output_unit", "unit") ?? "sp",
            quantity_good = Math.Round(quantityGood, 4),
            quantity_bad = Math.Round(quantityBad, 4)
        }
    };

        return JsonSerializer.Serialize(result, QrFormJsonOptions);
    }

    private static string? BuildSubProductLeftoversJsonFromFlatForm(IFormCollection form)
    {
        /*
         * Không tự lấy quantity_left chung để tạo leftover,
         * vì quantity_left có thể là của material hoặc reference input.
         *
         * Muốn tạo sub_product_leftovers từ field rời thì FE nên gửi rõ:
         * - leftover_process_code
         * - leftover_quantity_left
         */
        var processCode = FirstFormValue(
            form,
            "leftover_process_code",
            "leftoverProcessCode",
            "sub_product_process_code",
            "subProductProcessCode");

        if (string.IsNullOrWhiteSpace(processCode))
            return null;

        var quantityLeft = ReadDecimalAt(
            GetFormValues(
                form,
                "leftover_quantity_left",
                "leftoverQuantityLeft",
                "sub_product_quantity_left",
                "subProductQuantityLeft"),
            0);

        if (quantityLeft <= 0)
            return null;

        var result = new List<TaskSubProductLeftoverInputDto>
    {
        new TaskSubProductLeftoverInputDto
        {
            process_code = processCode.Trim(),
            process_name = FirstFormValue(
                form,
                "leftover_process_name",
                "leftoverProcessName",
                "sub_product_process_name",
                "subProductProcessName"),

            unit = FirstFormValue(
                form,
                "leftover_unit",
                "sub_product_unit",
                "unit") ?? "sp",

            quantity_left = Math.Round(quantityLeft, 4),

            note = FirstFormValue(
                form,
                "leftover_note",
                "sub_product_note",
                "note")
        }
    };

        return JsonSerializer.Serialize(result, QrFormJsonOptions);
    }

    private static List<string> GetFormValues(
        IFormCollection form,
        params string[] keys)
    {
        var result = new List<string>();

        foreach (var key in keys)
        {
            if (!form.TryGetValue(key, out var values))
                continue;

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            if (result.Count > 0)
                return result;
        }

        return result;
    }

    private static bool HasAnyKey(
        IFormCollection form,
        params string[] keys)
    {
        return keys.Any(key => form.ContainsKey(key));
    }

    private static string? FirstFormValue(
        IFormCollection form,
        params string[] keys)
    {
        return GetFormValues(form, keys).FirstOrDefault();
    }

    private static string? ReadStringAt(
        IReadOnlyList<string> values,
        int index)
    {
        if (values == null || values.Count == 0)
            return null;

        if (index < 0)
            index = 0;

        if (index >= values.Count)
            index = values.Count - 1;

        var value = values[index];

        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static int ReadIntAt(
        IReadOnlyList<string> values,
        int index)
    {
        var raw = ReadStringAt(values, index);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static decimal ReadDecimalAt(
        IReadOnlyList<string> values,
        int index)
    {
        var raw = ReadStringAt(values, index);

        if (string.IsNullOrWhiteSpace(raw))
            return 0m;

        raw = raw.Replace(",", ".");

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    private static bool ReadBoolAt(
        IReadOnlyList<string> values,
        int index,
        bool fallback)
    {
        var raw = ReadStringAt(values, index);

        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (bool.TryParse(raw, out var value))
            return value;

        if (raw == "1")
            return true;

        if (raw == "0")
            return false;

        return fallback;
    }
}