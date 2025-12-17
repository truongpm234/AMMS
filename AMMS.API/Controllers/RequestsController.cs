using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Orders;
using Microsoft.AspNetCore.Mvc;

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

        //[HttpPut("{id:int}")]
        //public async Task<IActionResult> Update(int id, [FromBody] CreateCustomerOrderResquest req)
        //{
        //    try
        //    {
        //        await _service.UpdateAsync(id, req);
        //        return Ok();
        //    }
        //    catch (Exception ex)
        //    {
        //        return BadRequest(new { message = ex.Message });
        //    }
        //}

        //[HttpDelete("{id:int}")]
        //public async Task<IActionResult> Delete(int id)
        //{
        //    await _service.DeleteAsync(id);
        //    return NoContent();
        //}
    }
}
