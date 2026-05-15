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
    public async Task<IActionResult> SuggestByQuery(
        [FromQuery] int? productTypeId,
        CancellationToken ct)
    {
        try
        {
            var result = await _service.SuggestAsync(productTypeId, ct);
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
}