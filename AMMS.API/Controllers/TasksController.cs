using AMMS.Application.Helpers;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Productions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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

        var t = await _taskRepo.GetByIdAsync(taskId);
        if (t == null)
        {
            return NotFound(new
            {
                message = "Task not found",
                task_id = taskId
            });
        }

        var policy = await _taskRepo.GetQtyPolicyAsync(taskId, ct);
        if (policy == null)
        {
            return BadRequest(new
            {
                message = "Không xác định được policy số lượng cho task.",
                task_id = taskId
            });
        }

        var bundle = await _scanSvc.GetTaskQrMaterialBundleAsync(taskId, ct);

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

            // Các field này giúp FE đối chiếu với Production Detail
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

            consumable_materials = bundle.consumable_materials,
            reference_inputs = bundle.reference_inputs
        });
    }

    [HttpPost("qr")]
    public async Task<ActionResult<TaskQrResponse>> CreateQr(
        [FromBody] CreateTaskQrRequest req,
        CancellationToken ct)
    {
        if (req == null)
        {
            return BadRequest(new
            {
                message = "Request body is required."
            });
        }

        if (req.task_id <= 0)
        {
            return BadRequest(new
            {
                message = "task_id không hợp lệ.",
                task_id = req.task_id
            });
        }

        var t = await _taskRepo.GetByIdAsync(req.task_id);
        if (t == null)
        {
            return NotFound(new
            {
                message = "Task not found",
                task_id = req.task_id
            });
        }

        var policy = await _taskRepo.GetQtyPolicyAsync(req.task_id, ct);
        if (policy == null)
        {
            return BadRequest(new
            {
                message = "Không xác định được ngưỡng số lượng hợp lệ cho công đoạn này.",
                task_id = req.task_id
            });
        }

        var ttlMinutes = req.ttl_minutes <= 0 ? 10 : req.ttl_minutes;
        var ttl = TimeSpan.FromMinutes(ttlMinutes);

        var isAuto = !req.qty_good.HasValue || req.qty_good.Value <= 0;

        int qtyGood;

        if (isAuto)
        {
            // suggested_qty từ TaskRepository.GetQtyPolicyAsync().
            // suggested_qty đã được đồng bộ theo logic Production:
            // - RALO: number_of_plates
            // - Từ CAT: sheets_total * n_up
            qtyGood = policy.suggested_qty;

            if (qtyGood <= 0)
                qtyGood = 1;
        }
        else
        {
            qtyGood = req.qty_good!.Value;

            if (qtyGood < policy.min_allowed || qtyGood > policy.max_allowed)
            {
                return BadRequest(new
                {
                    message =
                        $"Số lượng báo cáo không hợp lệ. " +
                        $"Công đoạn [{policy.process_code} - {policy.process_name}] " +
                        $"chỉ cho phép trong khoảng {policy.min_allowed} -> {policy.max_allowed} {policy.qty_unit}.",

                    task_id = req.task_id,
                    input_qty_good = qtyGood,

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
                    production_output_unit = policy.qty_unit
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

        var token = _tokenSvc.CreateToken(
            req.task_id,
            qtyGood,
            inputMaterials,
            ttl);

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();

        var qrMaterialBundle = await _scanSvc.GetTaskQrMaterialBundleAsync(req.task_id, ct);

        return Ok(new TaskQrResponse
        {
            task_id = req.task_id,
            token = token,
            expires_at_unix = expiresAt,

            qty_good_used = qtyGood,
            is_auto_filled = isAuto,

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
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<ActionResult<ScanTaskResult>> Finish(
    [FromForm] FinishTaskFormRequest req,
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
            var imageUrls = await UploadTaskReportImagesAsync(req.images, ct);

            var scanReq = new ScanTaskRequest
            {
                token = req.token.Trim(),
                reason = string.IsNullOrWhiteSpace(req.reason)
                    ? null
                    : req.reason.Trim(),

                // Lưu nhiều link cách nhau bằng dấu phẩy.
                // Không ảnh thì null.
                report_image_url = imageUrls.Count == 0
                    ? null
                    : string.Join(",", imageUrls)
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
}