namespace AMMS.Shared.DTOs.Productions
{
    public sealed class ProductionDetailDto
    {
        // Header
        public int prod_id { get; set; }
        public string? production_code { get; set; }
        public string? production_status { get; set; }
        public DateTime? start_date { get; set; }
        public DateTime? end_date { get; set; }
        public string? import_recieve_path { get; set; }

        // Order / Customer
        public int? order_id { get; set; }
        public string? order_code { get; set; }
        public DateTime? delivery_date { get; set; }
        public string customer_name { get; set; } = "";

        // Product
        public string? product_name { get; set; }
        public int quantity { get; set; }
        public string? packaging_standard { get; set; }
        public int? product_type_id { get; set; }
        public bool? is_full_process { get; set; }

        // Kích thước
        public int? length_mm { get; set; }
        public int? width_mm { get; set; }
        public int? height_mm { get; set; }
        public string? ready_print_file { get; set; }
        public string? ink_type_names { get; set; }
        public string? wave_type { get; set; }
        public string? paper_name { get; set; }
        public string? coating_type { get; set; }
        public string? paper_alternative { get; set; }
        public string? wave_alternative { get; set; }
        public int n_up { get; set; }

        // Timeline stages
        public DateTime? created_at { get; set; }
        public DateTime? planned_start_date { get; set; }
        public DateTime? actual_start_date { get; set; }
        public int? lamination_material_id { get; set; }
        public string? lamination_material_code { get; set; }
        public string? lamination_material_name { get; set; }
        public string? production_method { get; set; }
        public int? sub_product_id { get; set; }
        public int sub_product_used_qty { get; set; }
        public int nvl_qty { get; set; }
        public string? sub_product_process { get; set; }
        public string? production_approval_flow { get; set; }
        public bool all_tasks_finished { get; set; }

        public bool waiting_manual_importing { get; set; }
        public bool is_auto_production_approval { get; set; }
        public string? sub_product_issue_file { get; set; }
        public string? production_approval_label { get; set; } 
        public List<ProductionStageDto> stages { get; set; } = new();
    }
}

