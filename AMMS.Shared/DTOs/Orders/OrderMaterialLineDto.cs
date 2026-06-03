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

        public string? order_code { get; set; }

        public int order_request_id { get; set; }

        /*
         * NEW:
         * Kích thước sản phẩm thật của khách.
         * Lấy ưu tiên từ order_request.product_length_mm/product_width_mm/product_height_mm.
         */
        public int? product_length_mm { get; set; }

        public int? product_width_mm { get; set; }

        public int? product_height_mm { get; set; }

        /*
         * NEW:
         * Kích thước in/khổ in.
         * Lấy từ order_request.print_length_mm/print_width_mm.
         */
        public int? print_length_mm { get; set; }

        public int? print_width_mm { get; set; }

        /*
         * NEW:
         * Chuỗi hiển thị nhanh cho FE.
         */
        public string? product_size_text { get; set; }

        public string? print_size_text { get; set; }

        public List<OrderMaterialLineDto> items { get; set; } = new();
    }


}
