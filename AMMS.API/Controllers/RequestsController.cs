using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Infrastructure.Repositories;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Requests;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/requests")]
    public class RequestsController : ControllerBase
    {
        private readonly IRequestService _service;

        public RequestsController(IRequestService service)
        {
            _service = service;
        }

        [HttpPost]
        [ProducesResponseType(typeof(CreateRequestResponse), StatusCodes.Status201Created)]
        public async Task<IActionResult> Create([FromBody] CreateResquest req)
        {
            var result = await _service.CreateAsync(req);
            return StatusCode(StatusCodes.Status201Created, result);
        }

        [HttpPut("{id}")]
        [ProducesResponseType(typeof(UpdateRequestResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(UpdateRequestResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<UpdateRequestResponse>> UpdateAsync(int id, [FromBody] UpdateOrderRequest request)
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
        [HttpGet("paged")]
        public async Task<IActionResult> GetPaged([FromQuery] int page, [FromQuery] int pageSize)
        {
            var result = await _service.GetPagedAsync(page, pageSize);
            return Ok(result);
        }
        [HttpPost("{id:int}/convert-to-order")]
        public async Task<IActionResult> ConvertToOrder(int id)
        {
            var result = await _service.ConvertToOrderAsync(id);
            if (!result.Success) return BadRequest(result);
            return Ok(result);
        }
    }
}