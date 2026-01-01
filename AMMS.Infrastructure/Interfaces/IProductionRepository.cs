using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Productions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IProductionRepository
    {
        Task<DateTime?> GetNearestDeliveryDateAsync();
        Task AddAsync(production p);
        Task SaveChangesAsync();
        Task<production?> GetByIdAsync(int prodId);
        Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync( int page, int pageSize, CancellationToken ct = default);
        Task<ProductionProgressResponse> GetProgressAsync(int prodId);
        Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default);
        Task<bool> TryCloseProductionIfCompletedAsync(int prodId, DateTime now, CancellationToken ct = default);
    }
}
