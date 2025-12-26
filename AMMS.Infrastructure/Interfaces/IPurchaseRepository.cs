using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Purchases;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IPurchaseRepository
    {
        Task AddPurchaseAsync(purchase entity, CancellationToken ct = default);
        Task AddPurchaseItemsAsync(IEnumerable<purchase_item> items, CancellationToken ct = default);
        Task<bool> MaterialExistsAsync(int materialId, CancellationToken ct = default);
        Task<int> SaveChangesAsync(CancellationToken ct = default);

        Task<string> GenerateNextPurchaseCodeAsync(CancellationToken ct = default);

        Task<PagedResultLite<PurchaseOrderCardDto>> GetPurchaseOrdersAsync(
    string? status,
    int page,
    int pageSize,
    CancellationToken ct = default);


        Task<string?> GetSupplierNameAsync(int? supplierId, CancellationToken ct = default);
        Task<bool> SupplierExistsAsync(int supplierId, CancellationToken ct = default);
        Task<int?> GetManagerUserIdAsync(CancellationToken ct = default);

        Task<object> ReceiveAllPendingPurchasesAsync(
            int purchaseId, int managerUserId, CancellationToken ct = default);

        Task<PagedResultLite<PurchaseOrderListItemDto>> GetPendingPurchasesAsync(
            int page, int pageSize, CancellationToken ct = default);
    }
}
