using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ProductionMethodCostOptionDto
    {
        public string method { get; set; } = "";

        public bool is_available { get; set; }

        public int? sub_product_id { get; set; }

        public int sub_available_qty { get; set; }

        public int sub_used_qty { get; set; }

        public int nvl_qty { get; set; }

        public decimal unit_cost { get; set; }

        public decimal total_cost { get; set; }

        public decimal? saving_vs_nvl_unit { get; set; }

        public decimal? saving_vs_nvl_total { get; set; }

        public string? reason { get; set; }
    }
}
