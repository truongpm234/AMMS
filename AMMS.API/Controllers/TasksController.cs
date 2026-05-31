using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

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

    public TasksController(
        AppDbContext db,
        IHubContext<RealtimeHub> hub,
        ITaskRepository taskRepo,
        ITaskQrTokenService tokenSvc,
        ITaskScanService scanSvc,
        ITaskService taskService,
        ICloudinaryFileStorageService fileStorage)
    {
        _db = db;
        _taskRepo = taskRepo;
        _tokenSvc = tokenSvc;
        _scanSvc = scanSvc;
        _taskService = taskService;
        _hub = hub;
        _fileStorage = fileStorage;
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
         * - suggested_qty vẫn là group_total_qty, ví dụ 6000.
         * - max_allowed phải lấy theo actual_qty_prev_stage từ reference_inputs, ví dụ 7674.
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
                 * max_allowed = actual_qty_prev_stage
                 *
                 * Ví dụ:
                 * group_total_qty = 6000
                 * reference_inputs.actual_qty_prev_stage = 7674
                 * => max_allowed = 7674
                 */
                max_allowed = groupPolicy.MaxAllowed,
                suggested_qty = groupPolicy.SuggestedQty,
                happy_case_qty = groupPolicy.HappyCaseQty,

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
                reference_inputs = groupBundle.reference_inputs
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

    [HttpPost("qr")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<ActionResult<TaskQrResponse>> CreateQr(
    [FromForm] CreateTaskQrFormRequest form,
    CancellationToken ct)
    {
        List<TaskSubProductLeftoverInputDto> formSubProductLeftovers;

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
            formMaterials = ParseJsonList<TaskMaterialUsageInputDto>(form.materials_json);
            formRefs = ParseJsonList<TaskReferenceUsageInputDto>(form.reference_inputs_json);
            formOutputs = ParseJsonList<TaskOutputReportDto>(form.outputs_json);
            formSubProductLeftovers = ParseJsonList<TaskSubProductLeftoverInputDto>(form.sub_product_leftovers_json);

            imageUrls = await UploadTaskReportImagesAsync(form.images, ct);
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
            return BadRequest(new
            {
                message = "Chưa thể tạo QR vì công đoạn trước đó chưa hoàn thành.",
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
            qrReferenceInputs = BuildQrReferenceInputsWithSubProductLeftovers(
                formRefs,
                formSubProductLeftovers,
                taskMeta?.process?.process_code,
                taskMeta?.process?.process_name);
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
            string.Equals(taskMeta.prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase);

        var isManualTask =
            string.Equals(taskMeta.input_mode, "MANUAL", StringComparison.OrdinalIgnoreCase);

        var ttlMinutes = req.ttl_minutes <= 0 ? 10 : req.ttl_minutes;
        var ttl = TimeSpan.FromMinutes(ttlMinutes);

        /*
         * CASE 1: GROUP TASK
         * Group luôn manual. Materials/reference/output/reason/image được nhét vào token.
         */
        if (isGroupTask)
        {
            var maxAllowed = taskMeta.prod?.group_total_qty ?? 0;

            if (maxAllowed <= 0)
            {
                return BadRequest(new
                {
                    message = "Production ghép chưa có group_total_qty hợp lệ.",
                    task_id = req.task_id,
                    prod_id = taskMeta.prod_id
                });
            }

            var isAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

            var qtyGood = isAuto
                ? maxAllowed
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
                    message = "Số lượng báo cáo group vượt tổng số lượng production ghép.",
                    task_id = req.task_id,
                    input_qty_good = qtyGood,
                    max_allowed = maxAllowed,
                    suggested_qty = maxAllowed
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

            return Ok(new TaskQrResponse
            {
                task_id = req.task_id,
                token = token,
                expires_at_unix = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(),

                qty_good_used = qtyGood,
                is_auto_filled = isAuto,

                min_allowed = 1,
                max_allowed = maxAllowed,
                suggested_qty = maxAllowed,
                qty_unit = "sp",

                process_code = taskMeta.process?.process_code,
                process_name = taskMeta.process?.process_name,

                embedded_material_count = req.materials.Count,

                consumable_materials = new List<TaskConsumableMaterialDto>(),
                reference_inputs = new List<TaskReferenceInputDto>()
            });
        }

        /*
         * CASE 2: SINGLE MANUAL
         * FE chọn use_manual_input=true hoặc task input_mode=MANUAL.
         */
        if (req.use_manual_input || isManualTask)
        {
            var manualPolicy = await _taskRepo.GetQtyPolicyAsync(req.task_id, ct);
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

                if (qtyGood < manualPolicy.min_allowed || qtyGood > manualPolicy.max_allowed)
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

            return Ok(new TaskQrResponse
            {
                task_id = req.task_id,
                token = token,
                expires_at_unix = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(),

                qty_good_used = qtyGood,
                is_auto_filled = isAuto,

                min_allowed = manualPolicy.min_allowed,
                max_allowed = manualPolicy.max_allowed,
                suggested_qty = manualPolicy.suggested_qty,
                qty_unit = manualPolicy.qty_unit,

                process_code = manualPolicy.process_code,
                process_name = manualPolicy.process_name,

                embedded_material_count = req.materials.Count,

                consumable_materials = new List<TaskConsumableMaterialDto>(),
                reference_inputs = new List<TaskReferenceInputDto>()
            });
        }

        /*
         * CASE 3: SINGLE ESTIMATE FLOW CŨ
         * Materials vẫn validate/build theo estimate, nhưng reason/image cũng nhét vào token.
         */
        var policy = await _taskRepo.GetQtyPolicyAsync(req.task_id, ct);
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

            if (oldFlowQtyGood < policy.min_allowed || oldFlowQtyGood > policy.max_allowed)
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

        var qrMaterialBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(req.task_id, ct);

        return Ok(new TaskQrResponse
        {
            task_id = req.task_id,
            token = oldFlowToken,
            expires_at_unix = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(),

            qty_good_used = oldFlowQtyGood,
            is_auto_filled = oldFlowIsAuto,

            min_allowed = policy.min_allowed,
            max_allowed = policy.max_allowed,
            suggested_qty = policy.suggested_qty,
            qty_unit = policy.qty_unit,

            process_code = policy.process_code,
            process_name = policy.process_name,

            embedded_material_count = inputMaterials.Count,

            consumable_materials = qrMaterialBundle.consumable_materials,
            reference_inputs = qrMaterialBundle.reference_inputs
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
    List<TaskSubProductLeftoverInputDto>? subProductLeftovers,
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

        if (subProductLeftovers == null || subProductLeftovers.Count == 0)
            return result;

        var fallbackCode = NormQrProcessCode(currentProcessCode);

        foreach (var item in subProductLeftovers)
        {
            if (item.quantity_left <= 0)
                continue;

            var code = NormQrProcessCode(item.process_code);

            if (string.IsNullOrWhiteSpace(code))
                code = fallbackCode;

            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Không xác định được process_code để nhập BTP dư.");

            if (!CanImportAsSubProductStage(code))
            {
                throw new InvalidOperationException(
                    $"Không cho phép nhập kho bán thành phẩm ở công đoạn {code}. " +
                    $"Chỉ cho phép RALO,CAT,IN,PHU,CAN,CAN_MANG,BOI,BE,DUT. DAN là thành phẩm, không nhập vào sub_product.");
            }

            result.Add(new TaskReferenceUsageInputDto
            {
                input_code = code,
                input_name = string.IsNullOrWhiteSpace(item.process_name)
                    ? $"BTP dư sau công đoạn {code}"
                    : item.process_name.Trim(),

                unit = string.IsNullOrWhiteSpace(item.unit)
                    ? "sp"
                    : item.unit.Trim(),

                quantity_used = 0,
                quantity_left = Math.Round(item.quantity_left, 4)
            });
        }

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

        /*
         * Với GROUP + SUB:
         * - SuggestedQty = group_total_qty, ví dụ 6000.
         * - MaxAllowed = actual_qty_prev_stage, ví dụ 7674.
         */
        public int MaxAllowed { get; set; }
        public int SuggestedQty { get; set; }
        public int HappyCaseQty { get; set; }

        public string QtyUnit { get; set; } = "sp";
        public string Hint { get; set; } = "";
    }

    private static string NormQrCode(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }

    private static bool IsGroupSubTask(task taskMeta)
    {
        return taskMeta?.prod != null
               && string.Equals(taskMeta.prod.prod_kind, "GROUP", StringComparison.OrdinalIgnoreCase)
               && string.Equals(taskMeta.prod.prod_method, "SUB", StringComparison.OrdinalIgnoreCase);
    }

    private static int CeilPositive(decimal value)
    {
        return value <= 0
            ? 0
            : (int)Math.Ceiling(value);
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

        /*
         * Khi FE submit CreateQr, reference_inputs_json thường gửi quantity_used.
         * Với GROUP + SUB thì đây chính là input BTP thực tế user chọn.
         */
        return referenceInputs
            .Where(x => x != null)
            .Sum(x => x.quantity_used);
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
                MinAllowed = 1,
                MaxAllowed = 1,
                SuggestedQty = 1,
                HappyCaseQty = 1,
                QtyUnit = "sp",
                Hint = "Không tìm thấy production của task."
            };
        }

        var groupTotalQty = taskMeta.prod.group_total_qty > 0
            ? taskMeta.prod.group_total_qty
            : 1;

        /*
         * Default cho GROUP thường / GROUP + NVL:
         * Giữ logic cũ: validate theo group_total_qty.
         */
        var policy = new GroupQrQtyPolicy
        {
            IsGroupSub = false,
            MinAllowed = 1,
            MaxAllowed = groupTotalQty,
            SuggestedQty = groupTotalQty,
            HappyCaseQty = groupTotalQty,
            QtyUnit = "sp",
            Hint = "Task group cho phép nhập tay NVL, BTP input và output khi finish."
        };

        /*
         * FIX RIÊNG GROUP + SUB:
         * Không dùng group_total_qty làm max.
         * max_allowed phải lấy theo actual_qty_prev_stage của BTP đầu vào.
         */
        if (IsGroupSubTask(taskMeta))
        {
            var actualFromSubmitted = ResolveActualQtyFromSubmittedReferenceInputs(
                submittedReferenceInputs);

            var actualFromPrepared = ResolveActualQtyFromPreparedReferenceInputs(
                preparedReferenceInputs);

            decimal actualInputQty = 0m;

            if (actualFromSubmitted > 0)
                actualInputQty = actualFromSubmitted;
            else if (actualFromPrepared > 0)
                actualInputQty = actualFromPrepared;
            else
            {
                /*
                 * Fallback nếu caller chưa truyền preparedReferenceInputs.
                 */
                var bundle = await _scanSvc.GetTaskQrMaterialBundleAsync(
                    taskMeta.task_id,
                    ct);

                actualInputQty = ResolveActualQtyFromPreparedReferenceInputs(
                    bundle.reference_inputs);
            }

            var actualInputInt = CeilPositive(actualInputQty);

            /*
             * Nếu không có actual thì fallback group_total_qty để không vỡ API.
             * Nếu có actual, dùng actual làm max.
             */
            var maxByActualInput = actualInputInt > 0
                ? actualInputInt
                : groupTotalQty;

            policy.IsGroupSub = true;
            policy.MaxAllowed = maxByActualInput;

            /*
             * Suggested vẫn giữ số lượng sản phẩm đơn ghép.
             * Ví dụ suggested = 6000, max = 7674.
             */
            policy.SuggestedQty = groupTotalQty;
            policy.HappyCaseQty = groupTotalQty;

            policy.Hint =
                $"GROUP + SUB: suggested_qty lấy theo tổng SL đơn ghép = {groupTotalQty} sp; " +
                $"max_allowed lấy theo BTP thực tế đầu vào actual_qty_prev_stage = {maxByActualInput} sp.";
        }

        return policy;
    }
}