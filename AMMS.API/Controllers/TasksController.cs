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

        /*
         * FIX:
         * Không chỉ dùng _taskRepo.GetByIdAsync(taskId) vì có thể chưa Include:
         * - process
         * - prod
         *
         * API qr-prepare cần biết prod_kind/prod_method/process_code
         * để xử lý riêng GROUP + SUB.
         */
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

        var isGroupProduction =
            taskForPrepare.prod != null &&
            string.Equals(
                taskForPrepare.prod.prod_kind,
                "GROUP",
                StringComparison.OrdinalIgnoreCase);

        /*
         * CASE 1:
         * GROUP production
         *
         * Với GROUP + SUB:
         * - suggested_qty vẫn là group_total_qty, ví dụ 6000 -> 6290.
         * - max_allowed phải lấy theo 6290.
         *
         * Với GROUP + NVL/BOTH:
         * - giữ logic cũ, max_allowed theo group_total_qty.
         */
        if (isGroupProduction)
        {
            var groupBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
                taskId,
                ct);

            var groupPolicy = await ResolveGroupQrQtyPolicyAsync(
                taskMeta: taskForPrepare,
                preparedReferenceInputs: groupBundle.reference_inputs,
                submittedReferenceInputs: null,
                ct: ct);

            var groupStageCount = await _db.tasks
                .AsNoTracking()
                .CountAsync(x => x.prod_id == taskForPrepare.prod_id, ct);

            return Ok(new
            {
                task_id = taskId,

                process_code = taskForPrepare.process?.process_code,
                process_name = taskForPrepare.process?.process_name,

                qty_unit = groupPolicy.QtyUnit,
                min_allowed = groupPolicy.MinAllowed,

                /*
                 * GROUP + SUB:
                 * max_allowed = BTP planned input, ví dụ 6290.
                 */
                max_allowed = groupPolicy.MaxAllowed,
                suggested_qty = groupPolicy.SuggestedQty,
                happy_case_qty = groupPolicy.HappyCaseQty,

                /*
                 * order_qty vẫn là số lượng đơn ghép: 6000.
                 */
                order_qty = taskForPrepare.prod!.group_total_qty,

                sheets_required = 0,
                sheets_waste = 0,
                sheets_total = 0,
                n_up = 1,
                number_of_plates = 0,

                stage_index = taskForPrepare.seq_num ?? 1,
                stage_count = groupStageCount,

                production_output_qty = groupPolicy.SuggestedQty,
                production_output_unit = groupPolicy.QtyUnit,

                input_mode = "MANUAL",
                allow_manual_input = true,
                can_use_manual_input = true,
                manual_input_optional = false,

                is_group_production = true,
                group_prod_id = taskForPrepare.prod.prod_id,
                group_total_qty = taskForPrepare.prod.group_total_qty,

                manual_input_hint = groupPolicy.Hint,

                consumable_materials = groupBundle.consumable_materials,

                /*
                 * FIX:
                 * Không trả reference_inputs cũ có actual_qty_prev_stage = 7674.
                 */
                reference_inputs = groupPolicy.ReferenceInputs
            });
        }

        /*
         * CASE 2:
         * SINGLE / SPLIT production
         */
        var policy = await _taskRepo.GetQtyPolicyAsync(taskId, ct);

        if (policy == null)
        {
            return BadRequest(new
            {
                message = "Không xác định được policy số lượng cho task.",
                task_id = taskId
            });
        }

        var bundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
            taskId,
            ct);

        var inputMode = string.IsNullOrWhiteSpace(taskForPrepare.input_mode)
            ? "ESTIMATE"
            : taskForPrepare.input_mode.Trim();

        var forceManual = string.Equals(
            inputMode,
            "MANUAL",
            StringComparison.OrdinalIgnoreCase);

        return Ok(new
        {
            task_id = taskId,

            process_code = policy.process_code,
            process_name = policy.process_name,

            qty_unit = policy.qty_unit,
            min_allowed = policy.min_allowed,
            max_allowed = policy.max_allowed,
            suggested_qty = policy.suggested_qty,
            happy_case_qty = policy.happy_case_qty,

            order_qty = policy.order_qty,
            sheets_required = policy.sheets_required,
            sheets_waste = policy.sheets_waste,
            sheets_total = policy.sheets_total,
            n_up = policy.n_up,
            number_of_plates = policy.number_of_plates,

            stage_index = policy.stage_index,
            stage_count = policy.stage_count,

            production_output_qty = policy.suggested_qty,
            production_output_unit = policy.qty_unit,

            input_mode = inputMode,
            allow_manual_input = forceManual,
            can_use_manual_input = true,
            manual_input_optional = !forceManual,

            is_group_production = false,
            group_prod_id = (int?)null,
            group_total_qty = (int?)null,

            manual_input_hint = forceManual
                ? "Task này đang được cấu hình MANUAL, FE cần gửi manual input khi finish."
                : "Task SINGLE có thể chọn nhập tay input. Nếu không chọn thì dùng estimate như logic cũ.",

            consumable_materials = bundle.consumable_materials,
            reference_inputs = bundle.reference_inputs
        });
    }

    //[HttpPost("qr")]
    //[Consumes("multipart/form-data")]
    //[RequestSizeLimit(30 * 1024 * 1024)]
    //public async Task<ActionResult<TaskQrResponse>> CreateQr(
    //[FromForm] CreateTaskQrFormRequest form,
    //CancellationToken ct)
    //{
    //    List<TaskSubProductLeftoverInputDto> formSubProductLeftovers;

    //    if (form == null)
    //    {
    //        return BadRequest(new
    //        {
    //            message = "Request body is required."
    //        });
    //    }

    //    if (form.task_id <= 0)
    //    {
    //        return BadRequest(new
    //        {
    //            message = "task_id không hợp lệ.",
    //            task_id = form.task_id
    //        });
    //    }

    //    List<TaskMaterialUsageInputDto> formMaterials;
    //    List<TaskReferenceUsageInputDto> formRefs;
    //    List<TaskOutputReportDto> formOutputs;
    //    List<string> imageUrls;

    //    try
    //    {
    //        formMaterials = ParseJsonList<TaskMaterialUsageInputDto>(
    //            form.materials_json);

    //        formRefs = ParseJsonList<TaskReferenceUsageInputDto>(
    //            form.reference_inputs_json);

    //        formOutputs = ParseJsonList<TaskOutputReportDto>(
    //            form.outputs_json);

    //        formSubProductLeftovers = ParseJsonList<TaskSubProductLeftoverInputDto>(
    //            form.sub_product_leftovers_json);

    //        imageUrls = await UploadTaskReportImagesAsync(
    //            form.images,
    //            ct);
    //    }
    //    catch (InvalidOperationException ex)
    //    {
    //        return BadRequest(new
    //        {
    //            message = ex.Message,
    //            task_id = form.task_id
    //        });
    //    }

    //    var reportImageUrl = imageUrls.Count == 0
    //        ? null
    //        : string.Join(",", imageUrls);

    //    var req = new CreateTaskQrRequest
    //    {
    //        task_id = form.task_id,
    //        ttl_minutes = form.ttl_minutes,
    //        qty_good = form.qty_good,
    //        use_manual_input = form.use_manual_input,
    //        materials = formMaterials
    //    };

    //    var reason = string.IsNullOrWhiteSpace(form.reason)
    //        ? null
    //        : form.reason.Trim();

    //    var t = await _taskRepo.GetByIdAsync(req.task_id);

    //    if (t == null)
    //    {
    //        return NotFound(new
    //        {
    //            message = "Task not found",
    //            task_id = req.task_id
    //        });
    //    }

    //    var dep = await ProductionDependencyValidator.CheckTaskCanStartAsync(
    //        _db,
    //        req.task_id,
    //        ct);

    //    if (!dep.can_start)
    //    {
    //        var currentTaskMeta = await _db.tasks
    //            .AsNoTracking()
    //            .Include(x => x.process)
    //            .FirstOrDefaultAsync(x => x.task_id == req.task_id, ct);

    //        return BadRequest(new
    //        {
    //            message = "Chưa thể tạo QR vì công đoạn trước đó chưa hoàn thành.",
    //            task_id = req.task_id,
    //            prod_id = currentTaskMeta?.prod_id,
    //            process_code = currentTaskMeta?.process?.process_code,
    //            detail = dep.message,
    //            issues = dep.issues
    //        });
    //    }

    //    var taskMeta = await _db.tasks
    //        .AsNoTracking()
    //        .Include(x => x.prod)
    //        .Include(x => x.process)
    //        .FirstOrDefaultAsync(x => x.task_id == req.task_id, ct);

    //    if (taskMeta == null)
    //    {
    //        return NotFound(new
    //        {
    //            message = "Task not found",
    //            task_id = req.task_id
    //        });
    //    }

    //    List<TaskReferenceUsageInputDto> qrReferenceInputs;

    //    try
    //    {
    //        /*
    //         * reference_inputs_json gốc được giữ trong submitted_payload.reference_inputs.
    //         * qrReferenceInputs là bản đã merge sub_product_leftovers_json để đưa vào token.
    //         */
    //        qrReferenceInputs = BuildQrReferenceInputsWithSubProductLeftovers(
    //            formRefs,
    //            formSubProductLeftovers,
    //            taskMeta?.process?.process_code,
    //            taskMeta?.process?.process_name);
    //    }
    //    catch (InvalidOperationException ex)
    //    {
    //        return BadRequest(new
    //        {
    //            message = ex.Message,
    //            task_id = req.task_id
    //        });
    //    }

    //    var isGroupTask =
    //        taskMeta.prod != null &&
    //        string.Equals(
    //            taskMeta.prod.prod_kind,
    //            "GROUP",
    //            StringComparison.OrdinalIgnoreCase);

    //    var isManualTask =
    //        string.Equals(
    //            taskMeta.input_mode,
    //            "MANUAL",
    //            StringComparison.OrdinalIgnoreCase);

    //    var ttlMinutes = req.ttl_minutes <= 0
    //        ? 10
    //        : req.ttl_minutes;

    //    var ttl = TimeSpan.FromMinutes(ttlMinutes);

    //    /*
    //     * CASE 1: GROUP TASK
    //     * Group luôn manual.
    //     * Token chứa materials/referenceInputs/outputs/reason/images.
    //     * Response trả thêm submitted_payload để FE thấy lại đầy đủ JSON đã gửi.
    //     */
    //    if (isGroupTask)
    //    {
    //        var preparedBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
    //            req.task_id,
    //            ct);

    //        var groupPolicy = await ResolveGroupQrQtyPolicyAsync(
    //            taskMeta,
    //            preparedReferenceInputs: preparedBundle.reference_inputs,
    //            submittedReferenceInputs: qrReferenceInputs,
    //            ct: ct);

    //        var maxAllowed = groupPolicy.MaxAllowed;
    //        var suggestedQty = groupPolicy.SuggestedQty;

    //        if (maxAllowed <= 0)
    //        {
    //            return BadRequest(new
    //            {
    //                message = "Production ghép chưa có số lượng hợp lệ.",
    //                task_id = req.task_id,
    //                prod_id = taskMeta.prod_id
    //            });
    //        }

    //        var isAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

    //        var qtyGood = isAuto
    //            ? suggestedQty
    //            : req.qty_good!.Value;

    //        if (qtyGood <= 0)
    //        {
    //            return BadRequest(new
    //            {
    //                message = "Số lượng báo cáo phải lớn hơn 0.",
    //                task_id = req.task_id,
    //                input_qty_good = qtyGood
    //            });
    //        }

    //        if (qtyGood > maxAllowed)
    //        {
    //            return BadRequest(new
    //            {
    //                message =
    //                    $"Số lượng báo cáo group không hợp lệ. " +
    //                    $"Công đoạn [{taskMeta.process?.process_code} - {taskMeta.process?.process_name}] " +
    //                    $"chỉ cho phép tối đa {maxAllowed} {groupPolicy.QtyUnit}.",

    //                task_id = req.task_id,
    //                input_qty_good = qtyGood,
    //                max_allowed = maxAllowed,
    //                suggested_qty = suggestedQty,
    //                qty_unit = groupPolicy.QtyUnit,
    //                hint = groupPolicy.Hint
    //            });
    //        }

    //        var token = _tokenSvc.CreateToken(
    //            req.task_id,
    //            qtyGood,
    //            req.materials,
    //            ttl,
    //            useManualInput: true,
    //            reason: reason,
    //            reportImageUrl: reportImageUrl,
    //            referenceInputs: qrReferenceInputs,
    //            outputs: formOutputs);

    //        var response = new TaskQrResponse
    //        {
    //            task_id = req.task_id,
    //            token = token,
    //            expires_at_unix = DateTimeOffset.UtcNow
    //                .Add(ttl)
    //                .ToUnixTimeSeconds(),

    //            qty_good_used = qtyGood,
    //            is_auto_filled = isAuto,

    //            min_allowed = groupPolicy.MinAllowed,
    //            max_allowed = groupPolicy.MaxAllowed,
    //            suggested_qty = groupPolicy.SuggestedQty,
    //            qty_unit = groupPolicy.QtyUnit,

    //            process_code = taskMeta.process?.process_code,
    //            process_name = taskMeta.process?.process_name,

    //            embedded_material_count = req.materials.Count,

    //            consumable_materials = preparedBundle.consumable_materials,
    //            reference_inputs = groupPolicy.ReferenceInputs
    //        };

    //        return Ok(AttachSubmittedPayloadToQrResponse(
    //            response,
    //            form,
    //            formMaterials,
    //            formRefs,
    //            qrReferenceInputs,
    //            formOutputs,
    //            formSubProductLeftovers,
    //            imageUrls,
    //            reason,
    //            reportImageUrl));
    //    }

    //    /*
    //     * CASE 2: SINGLE MANUAL
    //     * FE chọn use_manual_input=true hoặc task input_mode=MANUAL.
    //     */
    //    if (req.use_manual_input || isManualTask)
    //    {
    //        var manualPolicy = await _taskRepo.GetQtyPolicyAsync(
    //            req.task_id,
    //            ct);

    //        if (manualPolicy == null)
    //        {
    //            return BadRequest(new
    //            {
    //                message = "Không xác định được ngưỡng số lượng hợp lệ cho công đoạn này.",
    //                task_id = req.task_id
    //            });
    //        }

    //        var isAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

    //        int qtyGood;

    //        if (isAuto)
    //        {
    //            qtyGood = manualPolicy.suggested_qty;

    //            if (qtyGood <= 0)
    //                qtyGood = 1;
    //        }
    //        else
    //        {
    //            qtyGood = req.qty_good!.Value;

    //            if (qtyGood < manualPolicy.min_allowed ||
    //                qtyGood > manualPolicy.max_allowed)
    //            {
    //                return BadRequest(new
    //                {
    //                    message =
    //                        $"Số lượng báo cáo không hợp lệ. " +
    //                        $"Công đoạn [{manualPolicy.process_code} - {manualPolicy.process_name}] " +
    //                        $"chỉ cho phép trong khoảng {manualPolicy.min_allowed} -> {manualPolicy.max_allowed} {manualPolicy.qty_unit}.",

    //                    task_id = req.task_id,
    //                    input_qty_good = qtyGood,
    //                    min_allowed = manualPolicy.min_allowed,
    //                    max_allowed = manualPolicy.max_allowed,
    //                    suggested_qty = manualPolicy.suggested_qty,
    //                    qty_unit = manualPolicy.qty_unit
    //                });
    //            }
    //        }

    //        var token = _tokenSvc.CreateToken(
    //            req.task_id,
    //            qtyGood,
    //            req.materials,
    //            ttl,
    //            useManualInput: true,
    //            reason: reason,
    //            reportImageUrl: reportImageUrl,
    //            referenceInputs: qrReferenceInputs,
    //            outputs: formOutputs);

    //        var response = new TaskQrResponse
    //        {
    //            task_id = req.task_id,
    //            token = token,
    //            expires_at_unix = DateTimeOffset.UtcNow
    //                .Add(ttl)
    //                .ToUnixTimeSeconds(),

    //            qty_good_used = qtyGood,
    //            is_auto_filled = isAuto,

    //            min_allowed = manualPolicy.min_allowed,
    //            max_allowed = manualPolicy.max_allowed,
    //            suggested_qty = manualPolicy.suggested_qty,
    //            qty_unit = manualPolicy.qty_unit,

    //            process_code = manualPolicy.process_code,
    //            process_name = manualPolicy.process_name,

    //            embedded_material_count = req.materials.Count,

    //            consumable_materials = new List<TaskConsumableMaterialDto>(),
    //            reference_inputs = new List<TaskReferenceInputDto>()
    //        };

    //        return Ok(AttachSubmittedPayloadToQrResponse(
    //            response,
    //            form,
    //            formMaterials,
    //            formRefs,
    //            qrReferenceInputs,
    //            formOutputs,
    //            formSubProductLeftovers,
    //            imageUrls,
    //            reason,
    //            reportImageUrl));
    //    }

    //    /*
    //     * CASE 3: SINGLE ESTIMATE FLOW CŨ
    //     * materials_json FE gửi có thể chỉ là override.
    //     * inputMaterials là materials thực sự được build/nhúng vào QR token.
    //     * submitted_payload vẫn trả lại đúng JSON FE đã nhập.
    //     */
    //    var policy = await _taskRepo.GetQtyPolicyAsync(
    //        req.task_id,
    //        ct);

    //    if (policy == null)
    //    {
    //        return BadRequest(new
    //        {
    //            message = "Không xác định được ngưỡng số lượng hợp lệ cho công đoạn này.",
    //            task_id = req.task_id
    //        });
    //    }

    //    var oldFlowIsAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

    //    int oldFlowQtyGood;

    //    if (oldFlowIsAuto)
    //    {
    //        oldFlowQtyGood = policy.suggested_qty;

    //        if (oldFlowQtyGood <= 0)
    //            oldFlowQtyGood = 1;
    //    }
    //    else
    //    {
    //        oldFlowQtyGood = req.qty_good!.Value;

    //        if (oldFlowQtyGood < policy.min_allowed ||
    //            oldFlowQtyGood > policy.max_allowed)
    //        {
    //            return BadRequest(new
    //            {
    //                message =
    //                    $"Số lượng báo cáo không hợp lệ. " +
    //                    $"Công đoạn [{policy.process_code} - {policy.process_name}] " +
    //                    $"chỉ cho phép trong khoảng {policy.min_allowed} -> {policy.max_allowed} {policy.qty_unit}.",

    //                task_id = req.task_id,
    //                input_qty_good = oldFlowQtyGood,

    //                min_allowed = policy.min_allowed,
    //                max_allowed = policy.max_allowed,
    //                suggested_qty = policy.suggested_qty,
    //                qty_unit = policy.qty_unit
    //            });
    //        }
    //    }

    //    List<TaskMaterialUsageInputDto> inputMaterials;

    //    try
    //    {
    //        inputMaterials = await _scanSvc.BuildMaterialUsageForQrAsync(
    //            req.task_id,
    //            req.materials,
    //            ct);
    //    }
    //    catch (InvalidOperationException ex)
    //    {
    //        return BadRequest(new
    //        {
    //            message = ex.Message,
    //            task_id = req.task_id,

    //            process_code = policy.process_code,
    //            process_name = policy.process_name,

    //            suggested_qty = policy.suggested_qty,
    //            qty_unit = policy.qty_unit
    //        });
    //    }

    //    var oldFlowToken = _tokenSvc.CreateToken(
    //        req.task_id,
    //        oldFlowQtyGood,
    //        inputMaterials,
    //        ttl,
    //        useManualInput: false,
    //        reason: reason,
    //        reportImageUrl: reportImageUrl,
    //        referenceInputs: qrReferenceInputs,
    //        outputs: formOutputs);

    //    var qrMaterialBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
    //        req.task_id,
    //        ct);

    //    var oldFlowResponse = new TaskQrResponse
    //    {
    //        task_id = req.task_id,
    //        token = oldFlowToken,
    //        expires_at_unix = DateTimeOffset.UtcNow
    //            .Add(ttl)
    //            .ToUnixTimeSeconds(),

    //        qty_good_used = oldFlowQtyGood,
    //        is_auto_filled = oldFlowIsAuto,

    //        min_allowed = policy.min_allowed,
    //        max_allowed = policy.max_allowed,
    //        suggested_qty = policy.suggested_qty,
    //        qty_unit = policy.qty_unit,

    //        process_code = policy.process_code,
    //        process_name = policy.process_name,

    //        /*
    //         * embedded_material_count là số material thực sự được nhúng token.
    //         * submitted_payload.materials vẫn là materials_json FE gửi.
    //         */
    //        embedded_material_count = inputMaterials.Count,

    //        consumable_materials = qrMaterialBundle.consumable_materials,
    //        reference_inputs = qrMaterialBundle.reference_inputs
    //    };

    //    return Ok(AttachSubmittedPayloadToQrResponse(
    //        oldFlowResponse,
    //        form,
    //        formMaterials,
    //        formRefs,
    //        qrReferenceInputs,
    //        formOutputs,
    //        formSubProductLeftovers,
    //        imageUrls,
    //        reason,
    //        reportImageUrl));
    //}

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

        var prod = taskMeta.prod;

        var groupTotalQty = prod.group_total_qty > 0
            ? prod.group_total_qty
            : 1;

        var refs = preparedReferenceInputs?.ToList()
            ?? new List<TaskReferenceInputDto>();

        if (refs.Count == 0)
        {
            var bundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
                taskMeta.task_id,
                ct);

            refs = bundle.reference_inputs?.ToList()
                ?? new List<TaskReferenceInputDto>();
        }

        /*
         * preparedQty lúc này đã được TaskScanService normalize theo flow:
         * - PHU: tổng input từ IN/SUB/MIXED.
         * - CAN: output PHU nếu PHU đã Finished, hoặc estimate PHU.
         * - BOI: output CAN nếu CAN đã Finished, hoặc estimate CAN.
         */
        var preparedQty = ResolveGroupPreparedQtyForQrPolicy(refs);

        var submittedQty = ResolveActualQtyFromSubmittedReferenceInputs(
            submittedReferenceInputs);

        var effectiveQtyDecimal = submittedQty > 0
            ? submittedQty
            : preparedQty;

        var effectiveQty = CeilPositive(effectiveQtyDecimal);

        if (effectiveQty <= 0)
            effectiveQty = groupTotalQty;

        var normalizedRefs = NormalizeGroupReferenceInputsForQrPolicy(
            refs,
            effectiveQty);

        var methodLabel = string.IsNullOrWhiteSpace(prod.prod_method)
            ? "GROUP"
            : prod.prod_method.Trim().ToUpperInvariant();

        return new GroupQrQtyPolicy
        {
            IsGroupSub = normalizedRefs.Count > 0,

            MinAllowed = 1,
            MaxAllowed = effectiveQty,
            SuggestedQty = effectiveQty,
            HappyCaseQty = effectiveQty,

            QtyUnit = "sp",

            ReferenceInputs = normalizedRefs,

            Hint =
                $"GROUP {methodLabel}: qty_good lấy theo flow tuần tự đã đồng bộ với groupProduction/detail. " +
                $"group_total_qty={groupTotalQty}; prepared_reference_qty={Math.Round(preparedQty, 4)}; " +
                $"submitted_reference_qty={Math.Round(submittedQty, 4)}; effective_qty={effectiveQty}. " +
                $"Công đoạn sau lấy output công đoạn trước làm input chuẩn."
        };
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