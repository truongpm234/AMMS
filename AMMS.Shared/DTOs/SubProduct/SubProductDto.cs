using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.SubProduct
{
    public class SubProductDto
    {
        public int id { get; set; }

        public int product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public int? width { get; set; }

        public int? length { get; set; }

        public string? product_process { get; set; }

        public int quantity { get; set; }

        public bool is_active { get; set; }

        public bool is_imported { get; set; }

        public string? import_file { get; set; }

        public int? source_task_id { get; set; }

        public int? source_task_log_id { get; set; }

        public int? source_prod_id { get; set; }

        public int? source_order_id { get; set; }

        public string? source_process_code { get; set; }

        public string? paper_material_code { get; set; }

        public string? wave_material_code { get; set; }

        public string? coating_material_code { get; set; }

        public string? lamination_material_code { get; set; }

        public string? material_signature { get; set; }

        public int? cost_estimate_id { get; set; }

        public decimal unit_cost_to_stage { get; set; }

        public decimal total_cost_to_stage { get; set; }

        public int? imported_to_sub_product_id { get; set; }

        public string? description { get; set; }

        public DateTime? updated_at { get; set; }
    }
}
