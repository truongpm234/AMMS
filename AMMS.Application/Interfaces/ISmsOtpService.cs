using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AMMS.Shared.DTOs.Auth.Auth;

namespace AMMS.Application.Interfaces
{
    public interface ISmsOtpService
    {
        Task<SendOtpSmsResponse> SendOtpAsync(SendOtpSmsRequest req, CancellationToken ct = default);
        Task<VerifyOtpSmsResponse> VerifyOtpAsync(VerifyOtpSmsRequest req, CancellationToken ct = default);
    }
}
