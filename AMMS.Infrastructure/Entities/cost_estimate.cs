using System;

namespace AMMS.Infrastructure.Entities;

public partial class cost_estimate
{
    public int estimate_id { get; set; }

    public int order_request_id { get; set; }

    // ==================== CHI PHÍ GIẤY ====================
    public decimal paper_cost { get; set; }

    public int paper_sheets_used { get; set; }

    public decimal paper_unit_price { get; set; }

    // ==================== CHI PHÍ MỰC IN ====================
    public decimal ink_cost { get; set; }

    public decimal ink_weight_kg { get; set; }

    public decimal ink_rate_per_m2 { get; set; }

    // ==================== CHI PHÍ KEO PHỦ ====================
    public decimal coating_glue_cost { get; set; }

    public decimal coating_glue_weight_kg { get; set; }

    public decimal coating_glue_rate_per_m2 { get; set; }

    public string coating_type { get; set; } = "NONE";

    // ==================== CHI PHÍ KEO BỒI ====================
    public decimal mounting_glue_cost { get; set; }

    public decimal mounting_glue_weight_kg { get; set; }

    public decimal mounting_glue_rate_per_m2 { get; set; }

    // ==================== CHI PHÍ MÀNG ====================
    public decimal lamination_cost { get; set; }

    public decimal lamination_weight_kg { get; set; }

    public decimal lamination_rate_per_m2 { get; set; }

    // ==================== TỔNG VẬT LIỆU ====================
    public decimal material_cost { get; set; }

    // ==================== KHẤU HAO (5%) ====================
    public decimal overhead_percent { get; set; }

    public decimal overhead_cost { get; set; }

    // ==================== CHI PHÍ CƠ BẢN ====================
    public decimal base_cost { get; set; }

    // ==================== RUSH ORDER ====================
    public bool is_rush { get; set; }

    public decimal rush_percent { get; set; }

    public decimal rush_amount { get; set; }

    public int days_early { get; set; }

    // ==================== TỔNG TRƯỚC CHIẾT KHẤU ====================
    public decimal subtotal { get; set; }

    // ==================== CHIẾT KHẤU ====================
    public decimal discount_percent { get; set; }

    public decimal discount_amount { get; set; }

    // ==================== TỔNG CUỐI ====================
    public decimal final_total_cost { get; set; }

    // ==================== THÔNG TIN KHÁC ====================
    public DateTime estimated_finish_date { get; set; }

    public DateTime desired_delivery_date { get; set; }

    public DateTime created_at { get; set; }

    // Chi tiết số lượng giấy
    public int sheets_required { get; set; }

    public int sheets_waste { get; set; }

    public int sheets_total { get; set; }

    // Diện tích
    public decimal total_area_m2 { get; set; }

    // Ghi chú
    public string? cost_note { get; set; }

    // Navigation
    public virtual order_request order_request { get; set; } = null!;
}