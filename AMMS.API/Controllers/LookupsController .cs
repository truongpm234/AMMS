using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LookupsController : ControllerBase
    {
        private readonly ILookupService _lookupService;

        public LookupsController(ILookupService lookupService)
        {
            _lookupService = lookupService;
        }

        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOtp([FromBody] OrderLookupSendOtpRequest req, CancellationToken ct)
        {
            try
            {
                await _lookupService.SendOtpForPhoneAsync(req.Phone, ct);
                return Ok(new { message = "Nếu số điện thoại tồn tại trong hệ thống, OTP đã được gửi đến email tương ứng." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("history")]
        public async Task<IActionResult> GetFullHistory([FromBody] RequestLookupWithOtpRequest req, CancellationToken ct)
        {
            try
            {
                var result = await _lookupService.GetHistoryByPhoneWithOtpAsync(
                    req.Phone,
                    req.Otp,
                    req.Page,
                    req.PageSize,
                    ct);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

    }
}
