using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using AMMS.Shared.DTOs.Requests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AMMS.Shared.DTOs.Auth.Auth;

namespace AMMS.Application.Services
{
    public class LookupService : ILookupService
    {
        private readonly IRequestRepository _requestRepo;
        private readonly ISmsOtpService _smsOtpService;

        public LookupService(
            IRequestRepository requestRepo, ISmsOtpService smsOtpService)
        {
            _requestRepo = requestRepo;
            _smsOtpService = smsOtpService;
        }

        public async Task SendOtpForPhoneAsync(string phone, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(phone))
                throw new ArgumentException("phone is required");

            phone = phone.Trim();

            var sendReq = new SendOtpSmsRequest(phone);
            var sendRes = await _smsOtpService.SendOtpAsync(sendReq, ct);

            if (!sendRes.success)
                throw new InvalidOperationException(sendRes.message ?? "Không gửi được OTP qua SMS.");
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

            var verifyReq = new VerifyOtpSmsRequest(phone, otp);
            var verifyRes = await _smsOtpService.VerifyOtpAsync(verifyReq, ct);

            if (!verifyRes.success || !verifyRes.valid)
                throw new InvalidOperationException(verifyRes.message ?? "OTP không hợp lệ hoặc đã hết hạn.");

            return await _requestRepo.GetOrdersByPhonePagedAsync(phone, page, pageSize, ct);
        }

        public async Task<PagedResultLite<RequestSortedDto>> GetRequestsByPhoneWithOtpAsync(
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
            var verifyReq = new VerifyOtpSmsRequest(phone, otp);
            var verifyRes = await _smsOtpService.VerifyOtpAsync(verifyReq, ct);

            if (!verifyRes.success || !verifyRes.valid)
                throw new InvalidOperationException(verifyRes.message ?? "OTP không hợp lệ hoặc đã hết hạn.");

            return await _requestRepo.GetRequestsByPhonePagedAsync(phone, page, pageSize, ct);
        }
    }
}
