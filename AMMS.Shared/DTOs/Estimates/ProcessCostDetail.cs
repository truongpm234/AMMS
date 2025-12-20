using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class ProcessCostDetail
    {
        public string process_name { get; set; } = null!;
        public int waste_sheets { get; set; }
        public decimal material_used_kg { get; set; }
        public decimal unit_price { get; set; }
        public decimal total_cost { get; set; }
        public string note { get; set; } = string.Empty;
    }
}
