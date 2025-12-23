using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Suppliers;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ISupplierService
    {
        // ✅ List supplier + materials match main_material_type
        Task<PagedResultLite<SupplierWithMaterialsDto>> GetPagedAsync(
            int page, int pageSize, CancellationToken ct = default);

        // Detail 1 supplier + materials history
        Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
            int supplierId, int page, int pageSize, CancellationToken ct = default);
    }
}
