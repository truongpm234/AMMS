using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Suppliers;

namespace AMMS.Application.Interfaces
{
    public interface ISupplierService
    {
        Task<PagedResultLite<SupplierLiteDto>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);

        Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
            int supplierId, int page, int pageSize, CancellationToken ct = default);
        Task<List<SupplierByMaterialIdDto>> ListSupplierByMaterialId(int id);
    }
}
