namespace AMMS.Shared.DTOs.Requests
{
    public class RequestDetailDto
    {
        public int request_id { get; set; }
        public string customer_name { get; set; } = "";
        public string customer_phone { get; set; } = "";
        public string? email { get; set; }
        public DateTime? delevery_date { get; set; }
        public string product_name { get; set; } = "";
        public int quantity { get; set; }
        public string? process_status { get; set; }
        public DateTime? request_date { get; set; }
        public string? description { get; set; }
        public string? design_file_path { get; set; }
        public string? detail_address { get; set; }
        public string? product_type { get; set; }
        public int? number_of_plates { get; set; }
        public string? production_processes { get; set; }
        public string? coating_type { get; set; }
        public string? paper_code { get; set; }
        public string? paper_name { get; set; }
        public string? wave_type { get; set; }
        public int? product_length_mm { get; set; }
        public int? product_width_mm { get; set; }
        public int? product_height_mm { get; set; }
        public int? glue_tab_mm { get; set; }
        public int? bleed_mm { get; set; }
        public bool? is_one_side_box { get; set; }
        public int? print_width_mm { get; set; }
        public int? print_height_mm { get; set; }
        public string? reason { get; set; }
        public List<CostEstimateDetailDto> cost_estimate { get; set; } = new();
    }

    public class CostEstimateDetailDto
    {
        public int estimate_id { get; set; }
        public decimal? final_total_cost { get; set; }
        public decimal? deposit_amount { get; set; }
        public List<ProcessCostDetailDto> process_cost { get; set; } = new();
    }

    public class ProcessCostDetailDto
    {
        public int process_cost_id { get; set; }
        public string process_code { get; set; } = "";
        public decimal? cost { get; set; }
    }
}