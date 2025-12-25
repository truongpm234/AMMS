using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IPurchaseService
    {
        Task<CreatePurchaseRequestResponse> CreatePurchaseRequestAsync(
            CreatePurchaseRequestDto dto,
            int? createdBy,
            CancellationToken ct = default);

        Task<PagedResultLite<PurchaseOrderListItemDto>> GetPurchaseOrdersAsync(int page, int pageSize, CancellationToken ct = default);

        Task<PurchaseOrderListItemDto> CreatePurchaseOrderAsync(
            CreatePurchaseRequestDto dto,
            CancellationToken ct = default);

        Task<object> ReceiveAllPendingPurchasesAsync(CancellationToken ct = default);

        Task<IReadOnlyList<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
        CancellationToken ct = default);
    }
}
