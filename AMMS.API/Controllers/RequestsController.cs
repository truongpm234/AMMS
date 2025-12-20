using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Email;
using AMMS.Shared.DTOs.Requests;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RequestsController : ControllerBase
    {
        private readonly IRequestService _service;
        private readonly IDealService _dealService;

        public RequestsController(IRequestService service, IDealService dealService)
        {
            _service = service;
            _dealService = dealService;
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

        [HttpPost("convert-to-order-by-{id:int}")]
        public async Task<IActionResult> ConvertToOrder(int id)
        {
            var result = await _service.ConvertToOrderAsync(id);
            if (!result.Success) return BadRequest(result);
            return Ok(result);
        }

        [HttpPost("send-deal")]
        public async Task<IActionResult> SendDealEmail([FromBody] SendDealEmailRequest req)
        {
            try
            {
                await _dealService.SendDealAndEmailAsync(req.RequestId);
                return Ok(new { message = "Sent deal email", orderRequestId = req.RequestId });
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ SendDealEmail failed:");
                Console.WriteLine(ex.Message);

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Send email failed",
                    detail = ex.Message,
                    orderRequestId = req.RequestId
                });
            }
        }

        [HttpGet("deal/accept")]
        public async Task<IActionResult> Accept([FromQuery] int orderRequestId, [FromQuery] string token)
        {
            await _dealService.AcceptDealAsync(orderRequestId);
            await _service.ConvertToOrderAsync(orderRequestId);
            return Ok("Bạn đã đồng ý báo giá. Nhân viên sẽ liên hệ sớm.");
        }

        [HttpGet("deal/reject")]
        public async Task<IActionResult> Reject([FromQuery] int orderRequestId, [FromQuery] string token, [FromQuery] string? reason)
        {
            await _dealService.RejectDealAsync(orderRequestId, reason ?? "No reason provided");
            return Ok("Bạn đã từ chối báo giá.");
        }

    }
}
