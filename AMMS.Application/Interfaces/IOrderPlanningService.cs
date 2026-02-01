using AMMS.Shared.DTOs.Planning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IOrderPlanningService
    {
        Task<EstimateFinishDateResponse?> EstimateFinishByOrderRequestAsync(int orderRequestId, CancellationToken ct = default);
    }
}
