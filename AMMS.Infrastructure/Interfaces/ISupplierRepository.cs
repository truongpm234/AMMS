using AMMS.Shared.DTOs.Suppliers;

namespace AMMS.Infrastructure.Interfaces
{
    public interface ISupplierRepository
    {
        Task<int> CountAsync(CancellationToken ct = default);

        // ⬇️ dùng cho list có kèm materials
        Task<List<SupplierLiteDto>> GetPagedAsync(
        int skip, int take, CancellationToken ct = default);

        Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
            int supplierId, int page, int pageSize, CancellationToken ct = default);
        Task<List<SupplierByMaterialIdDto>> ListSupplierByMaterialId(int id);
    }
}
