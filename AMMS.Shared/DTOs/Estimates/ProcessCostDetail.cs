using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class MaterialCostDetail
    {
        public string material_name { get; set; } = null!;
        public decimal quantity { get; set; }
        public string unit { get; set; } = null!;
        public decimal unit_price { get; set; }
        public decimal total_cost { get; set; }
        public string? note { get; set; }
    }
}
