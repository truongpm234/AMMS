using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IOrderLookupService
    {
        /// <summary>
        /// Gửi OTP tới email tương ứng với số điện thoại.
        /// </summary>
        Task SendOtpForPhoneAsync(string phone, CancellationToken ct = default);

        /// <summary>
        /// Verify OTP và trả về lịch sử đơn hàng (có phân trang).
        /// </summary>
        Task<PagedResultLite<OrderListDto>> GetOrdersByPhoneWithOtpAsync(
            string phone,
            string otp,
            int page,
            int pageSize,
            CancellationToken ct = default);
    }
}
