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
        Task<PagedResultLite<ProducingOrderCardDto>> GetProducingOrdersAsync(int page, int pageSize, int? roleId, CancellationToken ct = default);
        Task<ProductionDetailDto?> GetProductionDetailByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<ProductionWasteReportDto?> GetProductionWasteAsync(int prodId, CancellationToken ct = default);
        Task<bool> StartProductionByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<bool> SetProductionDeliveryAsync(int orderId, CancellationToken ct = default);
        Task<bool> SetCompletedAsync(int orderId, CancellationToken ct = default);
        Task<int?> StartProductionAndPromoteFirstTaskAsync(int orderId, CancellationToken ct = default);
        Task<List<MachineScheduleBoardDto>> GetMachineScheduleBoardAsync(DateTime from, DateTime to, CancellationToken ct = default);
        Task<ProductionReadyCheckResponse?> GetProductionReadyAsync(int orderId, CancellationToken ct = default);
        Task<GenerateImportReceiveResponse?> GenerateImportReceiveAsync(int orderId, CancellationToken ct = default);
        Task<SetProductionMethodResponse?> SetProductionMethodAsync(SetProductionMethodRequest req, CancellationToken ct = default);
        Task<int?> ScheduleTasksAfterMethodAsync(int orderId, CancellationToken ct = default);
        Task<bool> SetProductionReadyAsync(
    int orderId,
    bool isProductionReady,
    string? gmNote = null,
    string? proposedProductionMethod = null,
    CancellationToken ct = default);
    }
}
