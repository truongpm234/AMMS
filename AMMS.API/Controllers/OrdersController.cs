using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Orders;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly IOrderService _service;

        public OrdersController(IOrderService service)
        {
            _service = service;
        }

        [HttpPost("customer-create")]
        [ProducesResponseType(typeof(CreateCustomerOrderResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CustomerCreate([FromBody] CreateCustomerOrderRequest req)
        {
            try
            {
                var result = await _service.CreateCustomerOrderAsync(req);
                return StatusCode(StatusCodes.Status201Created, result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
