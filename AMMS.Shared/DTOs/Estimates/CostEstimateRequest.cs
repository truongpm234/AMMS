using AMMS.Shared.DTOs.Estimates;
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
        public AMMS.Shared.DTOs.Estimates.PaperEstimateResponse paper { get; set; } = null!;
        public DateTime desired_delivery_date { get; set; }

        // Thông tin để tính chi phí vật liệu
        public string product_type { get; set; } = null!;
        public string production_processes { get; set; } = null!;
        public string coating_type { get; set; } = "NONE";
        public bool has_lamination { get; set; } = false;
    }
}
