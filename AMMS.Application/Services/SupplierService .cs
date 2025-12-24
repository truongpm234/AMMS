using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Suppliers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class SupplierService : ISupplierService
    {
        private readonly ISupplierRepository _repo;

        public SupplierService(ISupplierRepository repo)
        {
            _repo = repo;
        }

        public async Task<PagedResultLite<SupplierWithMaterialsDto>> GetPagedAsync(
            int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 10;

            var skip = (page - 1) * pageSize;

            var list = await _repo.GetPagedWithMaterialsAsync(skip, pageSize + 1, ct);

            var hasNext = list.Count > pageSize;
            var data = hasNext ? list.Take(pageSize).ToList() : list;

            return new PagedResultLite<SupplierWithMaterialsDto>
            {
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext,
                Data = data
            };
        }

        public Task<SupplierDetailDto?> GetSupplierDetailWithMaterialsAsync(
            int supplierId, int page, int pageSize, CancellationToken ct = default)
            => _repo.GetSupplierDetailWithMaterialsAsync(supplierId, page, pageSize, ct);
    }
}
