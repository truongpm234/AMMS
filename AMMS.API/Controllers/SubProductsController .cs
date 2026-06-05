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
            var result = await _service.GetPagedAsync(
                page,
                pageSize,
                isActive,
                isImported,
                ct);

            return Ok(result);
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            [FromBody] CreateSubProductDto dto,
            CancellationToken ct)
        {
            try
            {
                var result = await _service.CreateAsync(dto, ct);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Create sub product failed",
                    detail = ex.Message
                });
            }
        }

        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(
            int id,
            [FromBody] UpdateSubProductDto dto,
            CancellationToken ct)
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
                return BadRequest(new { message = ex.Message, id });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Update sub product failed",
                    detail = ex.Message,
                    id
                });
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            var result = await _service.DeleteAsync(id, ct);

            if (!result.success)
                return NotFound(result);

            return Ok(result);
        }

        [HttpPut("generate-import-receipts")]
        public async Task<IActionResult> GenerateImportReceipts(
    [FromBody] GenerateSubProductImportReceiptsRequestDto dto,
    CancellationToken ct)
        {
            try
            {
                if (dto == null || dto.sub_product_ids == null || dto.sub_product_ids.Count == 0)
                {
                    return BadRequest(new
                    {
                        message = "sub_product_ids is required"
                    });
                }

                var result = await _service.GenerateImportReceiptsAsync(
                    dto.sub_product_ids,
                    ct);

                return Ok(result);
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
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    message = "Generate sub product import receipts failed",
                    detail = ex.Message
                });
            }
        }

        [HttpPut("import-pending")]
        public async Task<IActionResult> ImportPending(
            [FromQuery] List<int>? ids,
            CancellationToken ct)
        {
            try
            {
                var result = await _service.ImportPendingSubProductsAsync(
                    ids,
                    ct);

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
    }
}