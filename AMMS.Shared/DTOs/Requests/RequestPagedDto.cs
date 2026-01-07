using System;

namespace AMMS.Shared.DTOs.Requests
{
    public class RequestPagedDto
    {
        public int order_request_id { get; set; }

        public string customer_name { get; set; } = "";
        public string customer_phone { get; set; } = "";
        public string? customer_email { get; set; }
        public DateTime? delivery_date { get; set; }
        public string? product_name { get; set; }
        public int? quantity { get; set; }
        public string? description { get; set; }
        public string? design_file_path { get; set; }
        public string? detail_address { get; set; }
        public int? number_of_plates { get; set; }
        public string? coating_type { get; set; }
        public string? process_status { get; set; }
        public DateTime? order_request_date { get; set; }
        public decimal? final_cost { get; set; }
    }
}
