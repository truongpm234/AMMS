using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.DTOs.Productions.Groups;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

[ApiController]
[Route("api/[controller]")]
public class GroupProductionsController : ControllerBase
{
    private readonly IGroupProductionService _service;
    private readonly IProductionService _productionService;

    public GroupProductionsController(IGroupProductionService service, IProductionService productionService)
    {
        _service = service;
        _productionService = productionService;
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
        if (req == null)
            return BadRequest(new { message = "Request body is required." });

        try
        {
            int? userId = null;

            var rawUserId =
                User.FindFirst("userid")?.Value ??
                User.FindFirst("user_id")?.Value ??
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (int.TryParse(rawUserId, out var parsed))
                userId = parsed;

            var result = await _service.CreateAsync(
                req,
                userId,
                ct);

            /*
             * Yêu cầu mới:
             * Sau khi tạo group/split/private task xong,
             * tự động tạo phiếu xuất kho NVL/BTP và lưu file.
             *
             * ConfirmScheduleAsync group sẽ:
             * - tạo issue file chung.
             * - lưu vào GROUP.
             * - copy sang SINGLE member và SPLIT member nếu logic private của bạn đã có.
             */
            var confirmedIssues = new List<ConfirmProductionScheduleResponse>();

            foreach (var groupProdId in result.group_prod_ids.Distinct())
            {
                var confirm = await _productionService.ConfirmScheduleAsync(
                    groupProdId,
                    userId,
                    req.is_priority,
                    ct);

                confirmedIssues.Add(confirm);
            }

            /*
             * Nếu muốn chắc chắn split cũng có file riêng,
             * confirm thêm split. Nếu ConfirmGroupProductionIssueAsync của bạn đã copy file sang split,
             * đoạn này vẫn an toàn vì ConfirmSingleOrSplitProductionIssueAsync sẽ không phá task.
             */
            foreach (var splitProdId in result.split_prod_ids.Distinct())
            {
                var confirm = await _productionService.ConfirmScheduleAsync(
                    splitProdId,
                    userId,
                    req.is_priority,
                    ct);

                confirmedIssues.Add(confirm);
            }

            return Ok(new
            {
                result.group_prod_id,
                result.code,
                result.group_prod_ids,
                result.split_prod_ids,
                result.all_created_prod_ids,
                result.order_ids,
                result.warnings,
                result.message,

                is_priority = req.is_priority ?? false,

                issue_files = confirmedIssues
                    .Where(x => !string.IsNullOrWhiteSpace(x.issue_file))
                    .Select(x => new
                    {
                        x.prod_id,
                        x.prod_kind,
                        x.production_code,
                        x.issue_file
                    })
                    .ToList()
            });
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
}