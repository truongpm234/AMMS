using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class CostEstimateInsertRequest
    {
        public int order_request_id { get; set; }
        public int? previous_estimate_id { get; set; }

        // ----- Giấy -----
        public decimal? paper_cost { get; set; }
        public int? paper_sheets_used { get; set; }
        public decimal? paper_unit_price { get; set; }

        // ----- Mực -----
        public decimal? ink_cost { get; set; }
        public decimal? ink_weight_kg { get; set; }
        public decimal? ink_rate_per_m2 { get; set; }
        public string? ink_type_names { get; set; }

        // ----- Keo phủ -----
        public decimal? coating_glue_cost { get; set; }
        public decimal? coating_glue_weight_kg { get; set; }
        public decimal? coating_glue_rate_per_m2 { get; set; }
        public string? coating_type { get; set; }
        public string? coating_material_code { get; set; }
        public string? paper_code { get; set; }
        public string? paper_name { get; set; }
        public string? wave_type { get; set; }
        public int? wave_sheets_used { get; set; }

        // ----- Keo bồi -----
        public decimal? mounting_glue_cost { get; set; }
        public decimal? mounting_glue_weight_kg { get; set; }
        public decimal? mounting_glue_rate_per_m2 { get; set; }

        // ----- Màng cán -----
        public decimal? lamination_cost { get; set; }
        public decimal? lamination_weight_kg { get; set; }
        public decimal? lamination_rate_per_m2 { get; set; }
        public int? lamination_material_id { get; set; }
        public string? lamination_material_code { get; set; }
        public string? lamination_material_name { get; set; }

        // ----- Tổng vật liệu / khấu hao -----
        public decimal? material_cost { get; set; }
        public decimal? base_cost { get; set; }

        // ----- Rush -----
        public bool? is_rush { get; set; }
        public decimal? rush_percent { get; set; }
        public decimal? rush_amount { get; set; }
        public int? days_early { get; set; }

        // ----- Subtotal / discount / final -----
        public decimal? subtotal { get; set; }
        public decimal? discount_percent { get; set; }
        public decimal? discount_amount { get; set; }
        public decimal? final_total_cost { get; set; }

        // ----- Thời gian -----
        public DateTime? estimated_finish_date { get; set; }
        public DateTime? desired_delivery_date { get; set; }
        public DateTime? created_at { get; set; }
        public string? production_processes { get; set; }

        // ----- Số tờ / diện tích -----
        public int? sheets_required { get; set; }
        public int? sheets_waste { get; set; }
        public int? sheets_total { get; set; }
        public int? n_up { get; set; }
        public decimal deposit_amount { get; set; }
        public decimal? total_area_m2 { get; set; }

        // ----- Thiết kế -----
        public decimal? design_cost { get; set; }
        public string? cost_note { get; set; }
        public int? bleed_mm { get; set; }
        public int? glue_tab_mm { get; set; }
        public bool? is_one_side_box { get; set; }
        public int? print_length_mm { get; set; }
        public int? print_width_mm { get; set; }

        //màng+sóng
        public int? waste_gluing_boxes { get; set; }
        public decimal? sheet_area_m2 { get; set; }
        public int? print_sheets_used { get; set; }
        public decimal? total_coating_area_m2 { get; set; }
        public decimal? total_lamination_area_m2 { get; set; }
        public int? coating_sheets_used { get; set; }
        public int? lamination_sheets_used { get; set; }
        public decimal? wave_sheet_area_m2 { get; set; }
        public int? wave_n_up { get; set; }
        public int? wave_sheets_required { get; set; }
        public decimal? total_mounting_area_m2 { get; set; }
        public decimal? wave_unit_price { get; set; }
        public decimal? wave_cost { get; set; }
        public decimal? total_process_cost { get; set; }

        //Hop dong
        public string? contract_file_path { get; set; }
        public DateTime? contract_uploaded_at { get; set; }

        // ----- Chi tiết công đoạn (cost_estimate_process) -----
        public List<CostEstimateProcessDto>? process_costs { get; set; }
    }

    public class CostEstimateProcessDto
    {
        public string process_code { get; set; } = null!;
        public string? process_name { get; set; }
        public decimal? quantity { get; set; }
        public string? unit { get; set; }
        public decimal? unit_price { get; set; }
        public decimal? total_cost { get; set; }
        public string? note { get; set; }
    }
}

