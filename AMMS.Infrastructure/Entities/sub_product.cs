using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities
{
    [Table("sub_product", Schema = "AMMS_DB")]
    public class sub_product
    {
        public int id { get; set; }

        public int product_type_id { get; set; }

        public int? width { get; set; }

        public int? length { get; set; }

        public string? product_process { get; set; }

        public int quantity { get; set; }

        public bool is_active { get; set; } = true;

        public bool is_imported { get; set; } = true;

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

        public virtual product_type product_type { get; set; } = null!;
    }
}