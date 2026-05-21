using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.SubProduct;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SubProductsController : ControllerBase
    {
        private readonly ISubProductService _service;

        public SubProductsController(ISubProductService service)
        {
            _service = service;
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var result = await _service.GetByIdAsync(id, ct);
            if (result == null)
                return NotFound(new { message = "Sub product not found", id });

            return Ok(result);
        }

        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged(
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 10,
    [FromQuery] bool? isActive = null,
    [FromQuery] bool? isImported = null,
    CancellationToken ct = default)
        {
            var result = await _service.GetPagedAsync(page, pageSize, isActive, isImported, ct);
            return Ok(result);
        }

        [HttpPost("{id:int}/generate-import-receipt")]
        public async Task<IActionResult> GenerateImportReceipt(int id, CancellationToken ct)
        {
            try
            {
                var result = await _service.GenerateImportReceiptAsync(id, ct);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message, sub_product_id = id });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { message = ex.Message, sub_product_id = id });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Generate sub product import receipt failed",
                    detail = ex.Message,
                    sub_product_id = id
                });
            }
        }

        [HttpPost("import-pending")]
        public async Task<IActionResult> ImportPending(CancellationToken ct)
        {
            try
            {
                var result = await _service.ImportPendingSubProductsAsync(ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Import pending sub products failed",
                    detail = ex.Message
                });
            }
        }

        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] UpdateSubProductDto dto, CancellationToken ct)
        {
            try
            {
                var result = await _service.UpdateAsync(id, dto, ct);
                if (!result.success)
                    return NotFound(result);

                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new
                {
                    message = ex.Message,
                    id
                });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateSubProductDto dto, CancellationToken ct)
        {
            try
            {
                var result = await _service.CreateAsync(dto, ct);
                return StatusCode(StatusCodes.Status201Created, result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            var ok = await _service.DeleteAsync(id, ct);
            if (!ok)
            {
                return NotFound(new
                {
                    message = "Sub product not found",
                    id
                });
            }

            return Ok(new
            {
                success = true,
                message = "Sub product deleted successfully",
                id
            });
        }
    }
}
