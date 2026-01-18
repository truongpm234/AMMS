using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Auth
{
    public class Auth
    {
        public sealed record SendOtpSmsRequest(string phone);
        public sealed record SendOtpSmsResponse(bool success, string? message);

        public sealed record VerifyOtpSmsRequest(string phone, string otp);
        public sealed record VerifyOtpSmsResponse(bool success, bool valid, string? message);
    }
}
