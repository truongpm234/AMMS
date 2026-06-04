using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.PayOS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IDealService
    {
        Task SendDealAndEmailAsync(int orderRequestId, int? estimateId = null);
        Task RejectDealAsync(int orderRequestId, string reason);
        Task<string> AcceptAndCreatePayOsLinkAsync(int orderRequestId);
        Task SendConsultantStatusEmailAsync(order_request req, cost_estimate? est, string statusText, decimal? paidAmount = null, DateTime? paidAt = null, CancellationToken ct = default);
        Task NotifyConsultantPaidAsync(int orderRequestId, decimal paidAmount, DateTime paidAt);
        Task NotifyCustomerPaidAsync(int orderRequestId, decimal paidAmount, DateTime paidAt);
        Task<PayOsResultDto> CreateOrReuseDepositLinkAsync(int requestId, int estimateId, int? quoteId, CancellationToken ct = default);
        Task SendRemainingPaymentEmailAsync(int orderId, CancellationToken ct = default);
        Task<PayOsResultDto> CreateOrReuseRemainingPaymentLinkAsync(int orderId, CancellationToken ct = default);
        bool IsPaidStatus(string? status);
        Task NotifyRemainingPaidAsync(int orderId, decimal paidAmount, DateTime paidAt, CancellationToken ct = default);
        Task SendRemainingPaymentReminderEmailAsync(
    int orderId,
    CancellationToken ct = default);
    }
}
