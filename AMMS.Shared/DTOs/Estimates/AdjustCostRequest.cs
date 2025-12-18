using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class AdjustCostRequest
    {
        public decimal manual_adjust_cost { get; set; }
        public string? cost_note { get; set; }
    }
}
