using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class CostEstimateWithProcessResponse
    {
        public CostEstimateResponse cost { get; set; } = null!;
        public ProcessCostBreakdownResponse process_cost { get; set; } = null!;
    }
}
