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
        Task SendDealAndEmailAsync(int orderRequestId);
        Task RejectDealAsync(int orderRequestId, string reason);
        Task<string> AcceptAndCreatePayOsLinkAsync(int orderRequestId);
        Task SendConsultantStatusEmailAsync(order_request req, cost_estimate? est, string statusText, decimal? paidAmount = null, DateTime? paidAt = null);
        Task NotifyConsultantPaidAsync(int orderRequestId, decimal paidAmount, DateTime paidAt);
        Task NotifyCustomerPaidAsync(int orderRequestId, decimal paidAmount, DateTime paidAt);
        Task MarkAcceptedAsync(int orderRequestId);
        Task<PayOsDepositInfoDto> PrepareDepositPaymentAsync(int orderRequestId, CancellationToken ct = default);

    }
}
