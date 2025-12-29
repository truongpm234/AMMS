using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderResponseDto
    {
        public string order_id { get; set; } = "";
        public string? code { get; set; }
        public string customer_name { get; set; } = "";
        public string? product_name { get; set; }
        public string? product_id { get; set; }
        public int quantity { get; set; }
        public string created_at { get; set; } = "";
        public string delivery_date { get; set; } = "";
        public string? status { get; set; }
        public bool? can_fulfill { get; set; }
        public List<MissingMaterialDto>? missing_materials { get; set; }
    }
}
