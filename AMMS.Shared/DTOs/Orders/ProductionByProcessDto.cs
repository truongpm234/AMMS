using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class ProductionByProcessDto
    {
        public int prod_id { get; set; }
        public string? code { get; set; }
        public int? order_id { get; set; }
        public int? manager_id { get; set; }
        public DateTime? end_date { get; set; }
        public string? status { get; set; }
        public int? product_type_id { get; set; }
        public string? note { get; set; }
        public DateTime? created_at { get; set; }
        public DateTime? planned_start_date { get; set; }
        public DateTime? actual_start_date { get; set; }
        public bool? is_full_process { get; set; }
        public int sub_product_used_qty { get; set; }
        public string? import_recieve_path { get; set; }
        public int? sub_product_id { get; set; }
        public int nvl_qty { get; set; }
        public string? prod_method { get; set; }
        public string? gm_note { get; set; }
        public string? mgr_note { get; set; }
        public string? prod_kind { get; set; }
        public string? group_process_codes { get; set; }
        public int group_total_qty { get; set; }
        public string? gm_proposed_method { get; set; }

        public List<TaskByProcessDto> tasks { get; set; } = new();
    }
}
