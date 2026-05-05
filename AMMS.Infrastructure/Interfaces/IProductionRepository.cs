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
        Task<production?> GetByIdForUpdateAsync(int prodId, CancellationToken ct = default);
        Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync( int page, int pageSize, int? roleId, CancellationToken ct = default);
        Task<ProductionProgressResponse> GetProgressAsync(int prodId);
        Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default);
        Task<bool> TryCloseProductionIfCompletedAsync(int prodId, DateTime now, CancellationToken ct = default);
        Task<bool> StartProductionByOrderIdAsync(int orderId, DateTime now, CancellationToken ct = default);
        Task<bool> SetProductionDeliveryByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
        Task<List<MachineScheduleBoardDto>> GetMachineScheduleBoardAsync(DateTime from, DateTime to, CancellationToken ct = default);
        Task<bool> SetCompletedByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<production?> GetLatestByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<int?> StartProductionByOrderIdOnlyAsync(int orderId, DateTime now, CancellationToken ct = default);
        Task<ImportReceiveSourceDto?> GetImportReceiveSourceByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<bool> SaveImportReceivePathAsync(int prodId, string path, CancellationToken ct = default);
        Task<SetProductionMethodResponse?> SetProductionMethodAsync(SetProductionMethodRequest req, CancellationToken ct = default);
    }
}
