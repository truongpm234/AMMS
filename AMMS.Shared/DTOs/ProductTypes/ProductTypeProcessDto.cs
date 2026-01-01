using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.ProductTypes
{
    public class ProductTypeProcessDto
    {
        public int process_id { get; set; }
        public int seq_num { get; set; }
        public string process_name { get; set; } = "";
        public string? process_code { get; set; }
        public string? machine_code { get; set; }

        // cost rule (optional)
        public string? unit { get; set; }
        public decimal? unit_price { get; set; }

        // machine info (optional)
        public int? machine_quantity { get; set; }
        public int? capacity_per_hour { get; set; }
        public decimal? efficiency_percent { get; set; }
    }
}
