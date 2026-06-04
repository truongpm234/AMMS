using AMMS.Infrastructure.Entities;
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
        Task<bool> SetProductionDeliveryAsync(int orderId, CancellationToken ct = default);
        Task<bool> SetCompletedAsync(int orderId, CancellationToken ct = default);
        Task<int?> StartProductionAndPromoteFirstTaskByProdIdAsync(int prodId, CancellationToken ct = default);
        Task<List<MachineScheduleBoardDto>> GetMachineScheduleBoardAsync(DateTime from, DateTime to, CancellationToken ct = default);
        Task<ProductionReadyCheckResponse?> GetProductionReadyAsync(int orderId, CancellationToken ct = default);
        Task<GenerateImportReceiveBatchResponse?> GenerateImportReceiveAsync(int orderId, CancellationToken ct = default);
        Task<SetProductionMethodResponse?> SetProductionMethodAsync(SetProductionMethodRequest req, CancellationToken ct = default);
        Task<int?> ScheduleTasksAfterMethodAsync(int orderId, CancellationToken ct = default);
        Task<bool> SetProductionReadyAsync(int orderId, bool isProductionReady, string? gmNote = null, string? proposedProductionMethod = null, CancellationToken ct = default);
        Task<ProductionDetailDto?> GetProductionDetailByProdIdAsync(int prodId, CancellationToken ct = default);
        Task<List<production>> GetProductionsByTaskIdAsync(int taskId, CancellationToken ct = default);
        Task<ConfirmProductionScheduleResponse> ConfirmScheduleAsync(int prodId, int? confirmedByUserId, CancellationToken ct = default);
        Task<AutoCreatePendingProductionResultDto> AutoCreatePendingProductionAfterLayoutIfSingleMethodAsync(
            int orderId,
            CancellationToken ct = default);
        Task<ForceSetProductionImportingByProdIdResponse?> ForceSetProductionImportingByProdIdAsync(
    int prodId,
    CancellationToken ct = default);

        Task<GenerateImportReceiveFileResultDto?> GenerateImportReceiveFileAsync(
            int orderId,
            CancellationToken ct = default);

        Task<UploadImportReceiveFileResponse?> UploadImportReceiveFileAsync(
            int orderId,
            Stream fileStream,
            string fileName,
            string contentType,
            CancellationToken ct = default);
    }
}