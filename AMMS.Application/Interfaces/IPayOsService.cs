using AMMS.Shared.DTOs.PayOS;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IPayOsService
    {
        Task<string> CreatePaymentLinkAsync(
            int orderCode,
            int amount,
            string description,
            string buyerName,
            string buyerEmail,
            string buyerPhone,
            string returnUrl,
            string cancelUrl,
            CancellationToken ct = default);
        Task<PayOsPaymentInfo?> GetPaymentLinkInformationAsync(long orderCode, CancellationToken ct = default);

    }

}
