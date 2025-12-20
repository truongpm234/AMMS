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

        public PaperEstimateResponse paper { get; set; } = null!;

        public DateTime desired_delivery_date { get; set; }

        // Thông tin sản phẩm
        public string product_type { get; set; } = null!;

        public string? form_product { get; set; }

        public string production_processes { get; set; } = null!;

        public string coating_type { get; set; } = "KEO_NUOC";

        public bool has_lamination { get; set; } = false;

        /// <summary>
        /// Chiết khấu giảm giá (%, 0-100)
        /// Mặc định = 0 (không giảm giá)
        /// </summary>
        public decimal discount_percent { get; set; } = 0m;
    }
}
