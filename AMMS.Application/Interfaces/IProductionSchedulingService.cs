using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Shared.DTOs.Planning;

namespace AMMS.Application.Interfaces
{
    public interface IProductionSchedulingService
    {
        Task<int> ScheduleOrderAsync(int orderId, int productTypeId, string? productionProcessCsv, int? managerId = 3, bool isPriority = false, CancellationToken ct = default);

        Task<int> DispatchDueTasksAsync(CancellationToken ct = default);

        Task<ProductionSchedulePreviewDto?> PreviewByOrderRequestAsync(int orderRequestId, CancellationToken ct = default);
    }
}
