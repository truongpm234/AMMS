using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PurchasesController : ControllerBase
    {
        private readonly IPurchaseService _service;

        public PurchasesController(IPurchaseService service)
        {
            _service = service;
        }

        [HttpPost("request")]
        public async Task<IActionResult> CreatePurchaseRequest(
            [FromBody] CreatePurchaseRequestDto dto,
            CancellationToken ct)
        {
            int? createdBy = null;
            var result = await _service.CreatePurchaseRequestAsync(dto, createdBy, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }

        [HttpGet("orders")]
        public async Task<IActionResult> GetPurchaseOrders(
            [FromQuery] string? status = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetPurchaseOrdersAsync(status, page, pageSize, ct);
            return Ok(result);
        }

        [HttpPost("orders")]
        public async Task<IActionResult> CreatePurchaseOrder(
            [FromBody] CreatePurchaseRequestDto dto,
            CancellationToken ct)
        {
            var result = await _service.CreatePurchaseOrderAsync(dto, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }


        [HttpPut("orders/receive-all")]
        public async Task<IActionResult> ReceivePurchaseById(
             [FromQuery] int purchaseId,
             [FromBody] ReceivePurchaseRequestDto body,
             CancellationToken ct)
        {
            if (purchaseId <= 0)
                return BadRequest("purchaseId is required");

            if (body == null || string.IsNullOrWhiteSpace(body.status))
                return BadRequest("Request body status is required");

            var result = await _service.ReceiveAllPendingPurchasesAsync(purchaseId, body, ct);
            return Ok(result);
        }

        [HttpGet("pending")]
        public async Task<IActionResult> GetPendingPurchases(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10,
            CancellationToken ct = default)
        {
            var result = await _service.GetPendingPurchasesAsync(page, pageSize, ct);
            return Ok(result);
        }

        [HttpPut("orders/cancel")]
        public async Task<IActionResult> CancelPurchaseOrder(
    [FromQuery] int purchaseId,
    CancellationToken ct)
        {
            if (purchaseId <= 0)
                return BadRequest("purchaseId is required");

            var result = await _service.CancelPurchaseOrderAsync(purchaseId, ct);
            return Ok(result);
        }
    }
}
