using System;
using System.Collections.Generic;

namespace AMMS.Infrastructure.Entities;

public partial class cost_estimate
{
    public int estimate_id { get; set; }

    public int order_request_id { get; set; }

    public decimal paper_cost { get; set; }      // Chi phí giấy

    public decimal ink_cost { get; set; }       // Chi phí mực in

    public decimal coating_glue_cost { get; set; }       // Chi phí keo phủ

    public decimal mounting_glue_cost { get; set; }        // Chi phí keo bồi

    public decimal lamination_cost { get; set; }        // Chi phí màng

    public decimal material_cost { get; set; }          // Tổng chi phí vật liệu

    public decimal overhead_percent { get; set; }         // Chi phí khấu hao máy móc + khác (%)

    public decimal overhead_cost { get; set; }

    public decimal base_cost { get; set; }            // Chi phí cơ bản

    public bool is_rush { get; set; }             // Rush order

    public decimal rush_percent { get; set; }

    public decimal rush_amount { get; set; }

    public decimal system_total_cost { get; set; }             // Tổng chi phí

    public DateTime estimated_finish_date { get; set; }

    public DateTime desired_delivery_date { get; set; }

    public DateTime created_at { get; set; }

    public int sheets_required { get; set; }          // Chi tiết số lượng giấy

    public int sheets_waste { get; set; }

    public int sheets_total { get; set; }

    public decimal total_area_m2 { get; set; }            // Tổng diện tích in (m2)

    public decimal? manual_adjust_cost { get; set; }  

    public decimal? final_total_cost { get; set; } 

    public string? cost_note { get; set; }

    public virtual order_request order_request { get; set; } = null!;
}