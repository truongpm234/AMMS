using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class OrderLookupService : IOrderLookupService
    {
        private readonly IRequestRepository _requestRepo;
        private readonly IEmailService _emailService;

        public OrderLookupService(
            IRequestRepository requestRepo,
            IEmailService emailService)
        {
            _requestRepo = requestRepo;
            _emailService = emailService;
        }

        public async Task SendOtpForPhoneAsync(string phone, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(phone))
                throw new ArgumentException("phone is required");

            phone = phone.Trim();

            var email = await _requestRepo.GetEmailByPhoneAsync(phone, ct);
            if (email == null)
                throw new InvalidOperationException("Không tìm thấy email nào gắn với số điện thoại này.");

            await _emailService.SendOtpAsync(email);
        }

        public async Task<PagedResultLite<OrderListDto>> GetOrdersByPhoneWithOtpAsync(
            string phone,
            string otp,
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(phone))
                throw new ArgumentException("phone is required");
            if (string.IsNullOrWhiteSpace(otp))
                throw new ArgumentException("otp is required");

            phone = phone.Trim();

            // 1) Lấy email theo phone
            var email = await _requestRepo.GetEmailByPhoneAsync(phone, ct);
            if (email == null)
                throw new InvalidOperationException("Không tìm thấy email nào gắn với số điện thoại này.");

            // 2) Verify OTP theo email
            var ok = await _emailService.VerifyOtpAsync(email, otp);
            if (!ok)
                throw new InvalidOperationException("OTP không hợp lệ hoặc đã hết hạn.");

            // 3) Lấy lịch sử đơn hàng
            return await _requestRepo.GetOrdersByPhonePagedAsync(phone, page, pageSize, ct);
        }
    }
}
