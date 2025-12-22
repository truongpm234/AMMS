using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class ProcessCostBreakdownResponse
    {
        public int order_request_id { get; set; }
        public decimal total_cost { get; set; }
        public List<ProcessCostDetail> details { get; set; } = new();
    }
}
