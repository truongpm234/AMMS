using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Suppliers;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ISupplierService
    {
        Task<PagedResultLite<SupplierWithMaterialsDto>> GetPagedAsync(
            int page, int pageSize, CancellationToken ct = default);
        Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
            int supplierId, int page, int pageSize, CancellationToken ct = default);
    }
}
