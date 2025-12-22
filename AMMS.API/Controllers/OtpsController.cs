using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OtpsController : ControllerBase
    {
        private readonly IEmailService _emailService;
        public OtpsController(IEmailService emailService)
        {
            _emailService = emailService;
        }

        [HttpPost("send-otp")]
        public async Task<IActionResult> Send([FromBody] SendOtpRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.email))
                return BadRequest(new { message = "email is required" });

            await _emailService.SendOtpAsync(req.email);
            return Ok(new { message = "OTP sent" });
        }

        [HttpPost("verify-otp")]
        public async Task<IActionResult> Verify([FromBody] VerifyOtpRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.email) || string.IsNullOrWhiteSpace(req.otp))
                return BadRequest(new { message = "email and otp are required" });

            var ok = await _emailService.VerifyOtpAsync(req.email, req.otp);
            if (!ok)
                return BadRequest(new { message = "Invalid or expired OTP" });

            return Ok(new { message = "OTP verified" }); // ✅ 200
        }
    }
}
