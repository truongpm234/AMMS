using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Orders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PublicOrdersController : ControllerBase
    {
        private readonly IOrderLookupService _lookupService;

        public PublicOrdersController(IOrderLookupService lookupService)
        {
            _lookupService = lookupService;
        }

        /// <summary>
        /// Gửi OTP đến email của khách dựa trên số điện thoại dùng khi gửi yêu cầu in.
        /// </summary>
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

        /// <summary>
        /// Verify OTP và trả về lịch sử đơn hàng (phân trang)
        /// </summary>
        [HttpPost("history")]
        public async Task<IActionResult> GetHistory([FromBody] OrderLookupWithOtpRequest req, CancellationToken ct)
        {
            try
            {
                var result = await _lookupService.GetOrdersByPhoneWithOtpAsync(req.Phone, req.Otp, req.Page, req.PageSize, ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
