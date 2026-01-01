using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class CostEstimateRequest
    {
        public int order_request_id { get; set; }

        public DateTime desired_delivery_date { get; set; }

        public string product_type { get; set; } = null!;

        public string? form_product { get; set; }

        public string production_processes { get; set; } = null!;

        public string coating_type { get; set; } = "KEO_NUOC";

        //public bool has_lamination { get; set; } = false;

        public decimal discount_percent { get; set; } = 0m;

        public string? wave_type { get; set; }

        public PaperEstimateResponse paper { get; set; } = null!;

    }
}
