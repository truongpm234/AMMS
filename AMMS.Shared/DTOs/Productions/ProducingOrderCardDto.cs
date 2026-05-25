namespace AMMS.Shared.DTOs.Productions
{
    public class ProducingOrderCardDto
    {
        public int? order_id { get; set; }
        public string? code { get; set; }
        public string customer_name { get; set; } = "";
        public string? product_name { get; set; }
        public int quantity { get; set; }
        public DateTime? delivery_date { get; set; }
        public decimal progress_percent { get; set; }
        public string? current_stage { get; set; }
        public string? status { get; set; }
        public string? production_status { get; set; }
        public string? stage_status { get; set; } = null;
        public DateTime? planned_start_date { get; set; }
        public DateTime? actual_start_date { get; set; }
        public bool? is_production_ready { get; set; }
        public string? production_method { get; set; }
        public bool? is_full_process { get; set; }
        public int? sub_product_id { get; set; }
        public int sub_product_used_qty { get; set; }
        public string? prod_kind { get; set; }
        public string? production_code { get; set; }
        public bool is_group_production { get; set; }
        public bool is_split_production { get; set; }
        public string? gm_note { get; set; }
        public string? mgr_note { get; set; }
        public int nvl_qty { get; set; }
        public bool? can_start { get; set; }
        public string? can_start_message { get; set; }
        public List<ProductionStageStatusDto> stage_statuses { get; set; } = new();
        public List<string> stages { get; set; } = new();
        public int prod_id { get; set; }
        public string? group_status { get; set; }
        public string? group_process_codes { get; set; }
        public int? group_total_qty { get; set; }
        public int production_id { get; set; }
        public DateTime? created_at { get; set; }
        public string? production_approval_flow { get; set; }
        public bool is_auto_production_approval { get; set; }
        public string? production_approval_label { get; set; }
        public DateTime? start_date { get; set; }
        public DateTime? end_date { get; set; }
        public string? order_status { get; set; }
    }
}
