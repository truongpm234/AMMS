using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.AspNetCore.Http;
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

        // POST: api/purchases/request
        [HttpPost("request")]
        public async Task<IActionResult> CreatePurchaseRequest(
            [FromBody] CreatePurchaseRequestDto dto,
            CancellationToken ct)
        {
            // nếu bạn có JWT thì lấy user id từ claim
            int? createdBy = null;

            var result = await _service.CreatePurchaseRequestAsync(dto, createdBy, ct);
            return StatusCode(StatusCodes.Status201Created, result);
        }

        [HttpGet("orders")]
        public async Task<IActionResult> GetPurchaseOrders(
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 10,
    CancellationToken ct = default)
        {
            var result = await _service.GetPurchaseOrdersAsync(page, pageSize, ct);
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

        [HttpPost("orders/receive-all")]
        public async Task<IActionResult> ReceiveAllPendingPurchases(CancellationToken ct)
        {
            var result = await _service.ReceiveAllPendingPurchasesAsync(ct);
            return Ok(result);
        }
    }
}

