using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.SubProduct;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface ISubProductRepository
    {
        Task AddAsync(sub_product entity, CancellationToken ct = default);
        Task<sub_product?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<sub_product?> GetByIdTrackingAsync(int id, CancellationToken ct = default);
        Task<bool> ProductTypeExistsAsync(int productTypeId, CancellationToken ct = default);
        Task<int> SaveChangesAsync(CancellationToken ct = default);
        Task<PagedResultLite<SubProductDto>> GetPagedAsync(
            int page,
            int pageSize,
            bool? isActive = null,
            bool? isImported = null,
            CancellationToken ct = default);
    }
}

