namespace AMMS.Shared.DTOs.Productions
{
    public class ProducingOrderCardDto
    {
        public int order_id { get; set; }
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
        public string? gm_note { get; set; }
        public string? mgr_note { get; set; }
        public int nvl_qty { get; set; }
        public List<ProductionStageStatusDto> stage_statuses { get; set; } = new();
        public List<string> stages { get; set; } = new();
        public int prod_id { get; set; }
        public int? group_id { get; set; }
        public int? group_prod_id { get; set; }
        public string? group_code { get; set; }
        public string? group_status { get; set; }
        public string? group_process_codes { get; set; }
        public int? group_total_qty { get; set; }
        public bool is_grouped { get; set; }
        public bool is_active_grouped { get; set; }
        public List<int> group_prod_ids { get; set; } = new();
        public List<ProductionGroupInfoDto> group_productions { get; set; } = new();
    }
}
