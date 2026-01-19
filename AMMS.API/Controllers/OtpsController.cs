using AMMS.Application.Interfaces;
using AMMS.Shared.DTOs.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static AMMS.Shared.DTOs.Auth.Auth;

namespace AMMS.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OtpsController : ControllerBase
    {
        private readonly IEmailService _emailService;
        private readonly ISmsOtpService _otp;
        public OtpsController(IEmailService emailService, ISmsOtpService otp)
        {
            _emailService = emailService;
            _otp = otp;
        }

        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOTPSendRequest([FromBody] SendOtpRequest req)
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

            return Ok(new { message = "OTP verified" });
        }

        [HttpPost("sms/send")]
        public async Task<IActionResult> Send([FromBody] SendOtpSmsRequest req, CancellationToken ct)
        {
            var result = await _otp.SendOtpAsync(req, ct);
            if (!result.success) return BadRequest(result);
            return Ok(result);
        }

        // POST: /api/auth/otp/sms/verify
        [HttpPost("sms/verify")]
        public async Task<IActionResult> Verify([FromBody] VerifyOtpSmsRequest req, CancellationToken ct)
        {
            var result = await _otp.VerifyOtpAsync(req, ct);

            if (!result.success) return BadRequest(result);

            return Ok(new { valid = result.valid });
        }
    }
}
