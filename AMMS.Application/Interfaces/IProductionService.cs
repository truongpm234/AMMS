using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Productions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IProductionService
    {
        Task<NearestDeliveryResponse> GetNearestDeliveryAsync();
        Task<List<string>> GetAllProcessTypeAsync();
        Task<ProductionProgressResponse> GetProgressAsync(int prodId);
        Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(int page, int pageSize, CancellationToken ct = default);
        Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default);
    }
}
