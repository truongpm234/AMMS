using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IEstimateService
    {
        Task<PaperEstimateResponse> EstimatePaperAsync(PaperEstimateRequest req);
        Task<CostEstimateResponse> CalculateCostEstimateAsync(CostEstimateRequest req);
        Task AdjustCostBaseOnDiscountAsync(int estimateId, decimal? discountPercent, string? note);      
        Task<cost_estimate?> GetEstimateByIdAsync(int estimateId);
        Task<cost_estimate?> GetEstimateByOrderRequestIdAsync(int orderRequestId);
    }
}
