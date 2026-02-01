using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Requests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IEstimateService
    {
        Task UpdateFinalCostAsync(int orderRequestId, decimal? finalCostInput);
        Task<cost_estimate?> GetEstimateByIdAsync(int estimateId);
        Task<cost_estimate?> GetEstimateByOrderRequestIdAsync(int orderRequestId);
        Task<DepositByRequestResponse?> GetDepositByRequestIdAsync(int requestId, CancellationToken ct = default);
        Task<bool> OrderRequestExistsAsync(int order_request_id);
        Task SaveFeCostEstimateAsync(CostEstimateInsertRequest req, CancellationToken ct = default);
    }
}
