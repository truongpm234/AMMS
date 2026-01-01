using System;

namespace AMMS.Shared.DTOs.Requests
{
    public class CreateOrderRequestDto
    {
        // Thông tin khách hàng
        public string? customer_name { get; set; }
        public string? customer_phone { get; set; }
        public string? customer_email { get; set; }

        // Thông tin đơn / giao hàng
        public DateTime? delivery_date { get; set; }
        public string? detail_address { get; set; }

        // Sản phẩm
        public string? product_name { get; set; }
        public int? quantity { get; set; }
        public string? description { get; set; }

        // Thiết kế
        public string? design_file_path { get; set; }
        public bool? is_send_design { get; set; }

        // Thông tin kỹ thuật
        public string? product_type { get; set; }             // code: HOP_MAU, VO_HOP_GACH,...
        public int? number_of_plates { get; set; }            // số bản in
        public string? production_processes { get; set; }     // "IN,PHU,CAN_MANG,BOI,BE,DAN"
        public string? coating_type { get; set; }             // KE0_NUOC, UV, ...
        public string? paper_code { get; set; }
        public string? paper_name { get; set; }
        public string? wave_type { get; set; }

        // Kích thước thành phẩm
        public int? product_length_mm { get; set; }
        public int? product_width_mm { get; set; }
        public int? product_height_mm { get; set; }

        // Kích thước kỹ thuật
        public int? glue_tab_mm { get; set; }
        public int? bleed_mm { get; set; }
        public bool? is_one_side_box { get; set; }

        // Kích thước bản in (nếu đã tính trước)
        public int? print_width_mm { get; set; }
        public int? print_height_mm { get; set; }
    }
}
