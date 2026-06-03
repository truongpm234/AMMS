using AMMS.Shared.DTOs.Quotes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderDetailDto
    {
        public int order_id { get; set; }
        public string code { get; set; } = null!;
        public string status { get; set; } = null!;
        public string payment_status { get; set; } = null!;
        public DateTime order_date { get; set; }
        public DateTime? delivery_date { get; set; }

        public string customer_name { get; set; } = string.Empty;
        public string? customer_email { get; set; }
        public string? customer_phone { get; set; }
        public string? print_ready_file { get; set; }
        public string? detail_address { get; set; }

        public string product_name { get; set; } = string.Empty;
        public int quantity { get; set; }
        public string? ink_type_names { get; set; }
        public int? production_id { get; set; }
        public DateTime? production_start_date { get; set; }
        public DateTime? production_end_date { get; set; }
        public string approver_name { get; set; } = string.Empty;

        public string? specification { get; set; }
        public string? note { get; set; }

        public decimal final_total_cost { get; set; }
        public decimal deposit_amount { get; set; }  
        public decimal rush_amount { get; set; }

        public string? file_url { get; set; }       
        public string? contract_file { get; set; }
        public object? quote_fields { get; set; }

        public bool layout_confirmed { get; set; }

        public int? lamination_material_id { get; set; }
        public string? lamination_material_code { get; set; }
        public string? lamination_material_name { get; set; }
    }

}
