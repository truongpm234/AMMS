using AMMS.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrdersController : ControllerBase
    {
        private readonly IOrderService _service;

        public OrdersController(IOrderService service)
        {
            _service = service;
        }

        [HttpGet("get-by-{code}")]
        public async Task<IActionResult> GetByCOodeAsync(string code)
        {
            var order = await _service.GetOrderByCodeAsync(code);
            if (order == null)
            {
                return NotFound();
            }
            return Ok(order);
        }

        [HttpGet("get-order-by-{orderId}")]
        public async Task<IActionResult> GetOrderByIdAsync(int orderId)
        {
            var order = await _service.GetByIdAsync(orderId);
            if (order == null)
            {
                return NotFound();
            }
            return Ok(order);
        }
        [HttpGet("get-all-order")]
        public async Task<IActionResult> GetAllOrdersAsync()
        {
            var orders = await _service.GetAllAsync();
            return Ok(orders);
        }
    }
}
