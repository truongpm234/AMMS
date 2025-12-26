using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;

namespace AMMS.Application.Interfaces
{
    public interface IPurchaseService
    {
        Task<CreatePurchaseRequestResponse> CreatePurchaseRequestAsync(
            CreatePurchaseRequestDto dto,
            int? createdBy,
            CancellationToken ct = default);

<<<<<<< HEAD
        Task<PagedResultLite<PurchaseOrderListItemDto>> GetPurchaseOrdersAsync(
            int page, int pageSize, CancellationToken ct = default);

=======
        Task<PagedResultLite<PurchaseOrderWithItemsDto>> GetPurchaseOrdersAsync(
                    int page, int pageSize, CancellationToken ct = default);
>>>>>>> main
        Task<PurchaseOrderListItemDto> CreatePurchaseOrderAsync(
            CreatePurchaseRequestDto dto,
            CancellationToken ct = default);

        // ✅ CHANGED: receive theo purchaseId
        Task<object> ReceiveAllPendingPurchasesAsync(int purchaseId, CancellationToken ct = default);

<<<<<<< HEAD
        // ✅ CHANGED: pending paging
        Task<PagedResultLite<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
=======
        Task<PagedResultLite<PurchaseOrderWithItemsDto>> GetPendingPurchasesAsync(
>>>>>>> main
            int page, int pageSize, CancellationToken ct = default);
    }
}
