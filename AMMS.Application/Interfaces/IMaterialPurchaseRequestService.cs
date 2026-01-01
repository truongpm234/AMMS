using AMMS.Shared.DTOs.Purchases;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IMaterialPurchaseRequestService
    {
        Task<AutoPurchaseResultDto> CreateFromOrderAsync(int orderId, int managerId, CancellationToken ct = default);
    }
}
