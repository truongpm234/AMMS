using Microsoft.AspNetCore.Mvc;
using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Productions.Groups;
using System.Security.Claims;

[ApiController]
[Route("api/[controller]")]
public class GroupProductionsController : ControllerBase
{
    private readonly IGroupProductionService _service;

    public GroupProductionsController(IGroupProductionService service)
    {
        _service = service;
    }

    private int? GetCurrentUserId()
    {
        var raw =
            User.FindFirst("userid")?.Value ??
            User.FindFirst("user_id")?.Value ??
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return int.TryParse(raw, out var userId) ? userId : null;
    }

    [HttpGet("candidates")]
    public async Task<IActionResult> GetCandidates(
        [FromQuery] int? productTypeId,
        [FromQuery] string? processCodes,
        CancellationToken ct)
    {
        try
        {
            var result = await _service.GetCandidatesAsync(productTypeId, processCodes, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("suggestions")]
    public async Task<IActionResult> Suggest(
    [FromQuery] int? productTypeId,
    [FromQuery] string? processCodes,
    [FromQuery] string? orderIds,
    CancellationToken ct)
    {
        try
        {
            var result = await _service.SuggestAsync(
                productTypeId,
                processCodes,
                orderIds,
                ct);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(
    [FromBody] CreateGroupProductionRequest req,
    CancellationToken ct)
    {
        try
        {
            var result = await _service.PreviewAsync(req, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateGroupProductionRequest req,
        CancellationToken ct)
    {
        try
        {
            var result = await _service.CreateAsync(req, GetCurrentUserId(), ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{groupProdId:int}/start")]
    public async Task<IActionResult> Start(int groupProdId, CancellationToken ct)
    {
        try
        {
            await _service.StartAsync(groupProdId, ct);

            return Ok(new
            {
                message = "Đã bắt đầu production ghép.",
                prod_id = groupProdId,
                status = "InProcessing"
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{groupProdId:int}/detail")]
    public async Task<IActionResult> Detail(
    int groupProdId,
    CancellationToken ct)
    {
        try
        {
            var result = await _service.GetDetailAsync(groupProdId, ct);

            if (result == null)
                return NotFound(new { message = "Group production not found." });

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("task/{taskId:int}/context")]
    public async Task<IActionResult> TaskContext(int taskId, CancellationToken ct)
    {
        try
        {
            var result = await _service.GetTaskContextAsync(taskId, ct);

            if (result == null)
                return NotFound(new { message = "Task not found." });

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private static bool IsTaskStartedForGroupSuggestion(
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
         * Chỉ Unassigned/null được xem là chưa bắt đầu.
         * Ready cũng loại, vì Ready thường đã giữ máy / đã cho phép báo cáo.
         */
        return s != "UNASSIGNED";
    }

    private async Task<HashSet<int>> LoadSingleProdIdsHavingStartedTaskAsync(
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
            .Where(x => IsTaskStartedForGroupSuggestion(
                x.status,
                x.start_time,
                x.end_time,
                logSet.Contains(x.task_id)))
            .Select(x => x.prod_id)
            .Distinct()
            .ToHashSet();
    }

    private static void ValidateRowsHaveNoStartedTaskOrThrow(
        List<GroupOrderRow> rows,
        string actionName)
    {
        var invalidRows = rows
            .Where(x => x.HasAnyStartedTask)
            .Select(x =>
                $"order_id={x.Order.order_id}, single_prod_id={x.SingleProd.prod_id}")
            .ToList();

        if (invalidRows.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Không thể {actionName} vì có order đã bắt đầu ít nhất một công đoạn sản xuất. " +
            $"Các order đã bắt đầu không được ghép/tách production. Chi tiết: {string.Join(" | ", invalidRows)}");
    }
}