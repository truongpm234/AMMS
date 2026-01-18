using AMMS.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Twilio;
using Twilio.Rest.Verify.V2.Service;
using static AMMS.Shared.DTOs.Auth.Auth;

namespace AMMS.Application.Services
{
    public class TwilioSmsOtpService : ISmsOtpService
    {
        private readonly string _accountSid;
        private readonly string _authToken;
        private readonly string _verifyServiceSid;

        public TwilioSmsOtpService(IConfiguration config)
        {
            _accountSid = config["Twilio:AccountSid"] ?? throw new ArgumentException("Missing Twilio:AccountSid");
            _authToken = config["Twilio:AuthToken"] ?? throw new ArgumentException("Missing Twilio:AuthToken");
            _verifyServiceSid = config["Twilio:VerifyServiceSid"] ?? throw new ArgumentException("Missing Twilio:VerifyServiceSid");

            TwilioClient.Init(_accountSid, _authToken);
        }

        // Chuẩn hoá phone về E.164: +84...
        // Bạn có thể làm chặt hơn, nhưng basic thế này đủ test.
        private static string NormalizePhone(string phone)
        {
            phone = (phone ?? "").Trim();
            if (string.IsNullOrWhiteSpace(phone))
                throw new ArgumentException("phone is required");

            // Nếu đã có + thì assume đúng E.164
            if (phone.StartsWith("+"))
                return phone;

            // Nếu user nhập 0xxxxxxxxx => đổi sang +84xxxxxxxxx
            if (phone.StartsWith("0"))
                return "+84" + phone.Substring(1);

            // Nếu user nhập 84xxxxxxxxx => +84...
            if (phone.StartsWith("84"))
                return "+" + phone;

            // fallback: bạn có thể throw để bắt user nhập đúng
            return phone;
        }

        public async Task<SendOtpSmsResponse> SendOtpAsync(SendOtpSmsRequest req, CancellationToken ct = default)
        {
            try
            {
                var to = NormalizePhone(req.phone);

                // Twilio Verify: tạo verification (gửi OTP)
                var verification = await VerificationResource.CreateAsync(
                    to: to,
                    channel: "sms",
                    pathServiceSid: _verifyServiceSid
                );

                // verification.Status thường: pending
                if (string.Equals(verification.Status, "pending", StringComparison.OrdinalIgnoreCase))
                    return new SendOtpSmsResponse(true, "OTP sent");

                // vẫn coi là success nếu Twilio trả khác pending nhưng không lỗi
                return new SendOtpSmsResponse(true, $"OTP status: {verification.Status}");
            }
            catch (Twilio.Exceptions.ApiException ex)
            {
                // Trial hay dính lỗi: số chưa verified, geo permissions, ...
                return new SendOtpSmsResponse(false, $"Twilio error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new SendOtpSmsResponse(false, ex.Message);
            }
        }

        public async Task<VerifyOtpSmsResponse> VerifyOtpAsync(VerifyOtpSmsRequest req, CancellationToken ct = default)
        {
            try
            {
                var to = NormalizePhone(req.phone);
                var code = (req.otp ?? "").Trim();

                if (string.IsNullOrWhiteSpace(code))
                    return new VerifyOtpSmsResponse(false, false, "otp is required");

                // Twilio Verify: check OTP
                var check = await VerificationCheckResource.CreateAsync(
                    to: to,
                    code: code,
                    pathServiceSid: _verifyServiceSid
                );

                // check.Status thường: approved / pending
                var ok = string.Equals(check.Status, "approved", StringComparison.OrdinalIgnoreCase);

                return new VerifyOtpSmsResponse(true, ok, ok ? "OTP valid" : "OTP invalid");
            }
            catch (Twilio.Exceptions.ApiException ex)
            {
                return new VerifyOtpSmsResponse(false, false, $"Twilio error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new VerifyOtpSmsResponse(false, false, ex.Message);
            }
        }
    }
}
