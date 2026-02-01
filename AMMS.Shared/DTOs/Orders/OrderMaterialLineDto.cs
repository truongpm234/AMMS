using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderMaterialLineDto
    {
        public string material_group { get; set; } = "";
        public string? material_code { get; set; }
        public string? material_name { get; set; }
        public string unit { get; set; } = "";
        public decimal quantity { get; set; }
    }

    public class OrderMaterialsResponse
    {
        public int order_id { get; set; }
        public int? order_request_id { get; set; }
        public string? order_code { get; set; }

        public List<OrderMaterialLineDto> items { get; set; } = new();
    }
}
