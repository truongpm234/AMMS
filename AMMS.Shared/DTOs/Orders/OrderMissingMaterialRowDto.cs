using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderMissingMaterialRowDto
    {
        public int order_id { get; set; }
        public string? order_code { get; set; }

        // ✅ ngày tạo yêu cầu: ưu tiên order_request.order_request_date, fallback order.order_date
        public DateTime? request_date { get; set; }

        public string material_code { get; set; } = "";     // ✅ mã NVL
        public decimal missing_qty { get; set; }            // ✅ SL thiếu

        // ✅ xài MissingMaterialDto theo yêu cầu
        public MissingMaterialDto material { get; set; } = new();
    }
}
