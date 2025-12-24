using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.Purchases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrdersController : ControllerBase
    {
        private readonly IOrderService _service;
        private readonly IMaterialPurchaseRequestService _materialPurchaseService;


        public OrdersController(IOrderService service, IMaterialPurchaseRequestService materialPurchaseService)
        {
            _service = service;
            _materialPurchaseService = materialPurchaseService;
        }

        [HttpGet("get-by-{code}")]
        public async Task<IActionResult> GetByCodeAsync(string code)
        {
            var order = await _service.GetOrderByCodeAsync(code);
            if (order == null)
            {
                return NotFound();
            }
            return Ok(order);
        }

        //[HttpGet("get-order-by-{orderId}")]
        //public async Task<IActionResult> GetOrderByIdAsync(int orderId)
        //{
        //    var order = await _service.GetByIdAsync(orderId);
        //    if (order == null)
        //    {
        //        return NotFound();
        //    }
        //    return Ok(order);
        //}

        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            var result = await _service.GetPagedAsync(page, pageSize);
            return Ok(result);
        }

        [HttpGet("detail/{id:int}")]
        public async Task<IActionResult> GetDetail(int id, CancellationToken ct)
        {
            var dto = await _service.GetDetailAsync(id);
            if (dto == null)
                return NotFound(new { message = "Order not found" });

            return Ok(dto);
        }

        [HttpPost("{orderId:int}/auto-purchase")]
        public async Task<IActionResult> CreateAutoPurchase(
            int orderId,
            [FromBody] AutoPurchaseFromOrderRequest req,
            CancellationToken ct)
        {
            try
            {
                var result = await _materialPurchaseService.CreateFromOrderAsync(
                    orderId,
                    req.ManagerId,
                    ct);

                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // Ví dụ: không thiếu NVL
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
