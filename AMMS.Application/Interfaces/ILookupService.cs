using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ILookupService
    {
        Task SendOtpForPhoneAsync(string phone, CancellationToken ct = default);

        Task<PhoneHistoryWithOtpResult> GetHistoryByPhoneWithOtpAsync(
        string phone,
        string otp,
        int page,
        int pageSize,
        CancellationToken ct = default);
    }
}
