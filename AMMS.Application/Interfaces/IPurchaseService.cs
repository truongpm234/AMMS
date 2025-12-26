using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;

namespace AMMS.Application.Interfaces
{
    public interface IPurchaseService
    {
        Task<CreatePurchaseRequestResponse> CreatePurchaseRequestAsync(CreatePurchaseRequestDto dto, int? createdBy, CancellationToken ct = default);
        Task<PagedResultLite<PurchaseOrderCardDto>> GetPurchaseOrdersAsync(string? status, int page, int pageSize, CancellationToken ct = default);
        Task<PagedResultLite<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(int page, int pageSize, CancellationToken ct = default);
        Task<PurchaseOrderListItemDto> CreatePurchaseOrderAsync(CreatePurchaseRequestDto dto, CancellationToken ct = default);
        Task<object> ReceiveAllPendingPurchasesAsync(int purchaseId, CancellationToken ct = default);
    }
}
