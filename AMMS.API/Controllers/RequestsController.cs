using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
using AMMS.Shared.DTOs.Orders;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RequestsController : ControllerBase
    {
        private readonly IRequestService _service;

        public RequestsController(IRequestService service)
        {
            _service = service;
        }

        [HttpPost]
        [ProducesResponseType(typeof(CreateCustomerOrderResponse), StatusCodes.Status201Created)]
        public async Task<IActionResult> Create([FromBody] CreateCustomerOrderResquest req)
        {
            var result = await _service.CreateAsync(req);
            return StatusCode(StatusCodes.Status201Created, result);
        }

        [HttpPut("{id}")]
        [ProducesResponseType(typeof(UpdateOrderRequestResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(UpdateOrderRequestResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<UpdateOrderRequestResponse>> UpdateAsync(int id, [FromBody] UpdateOrderRequest request)
        {
            var update = await _service.UpdateAsync(id, request);
            return StatusCode(StatusCodes.Status200OK, update);
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            await _service.DeleteAsync(id);
            return NoContent();           
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var order = await _service.GetByIdAsync(id);
            if (order == null)
                return NotFound(new { message = "Order request not found" });

            return Ok(order);
        }
    }
}