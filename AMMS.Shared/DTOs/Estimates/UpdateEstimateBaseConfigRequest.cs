using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace AMMS.Shared.DTOs.Estimates
{
    public class UpdateEstimateBaseConfigRequest
    {
        // Nhóm cấu hình giá vật tư.
        public MaterialPricesUpdateDto? material_prices { get; set; }

        // Nhóm cấu hình định mức vật tư.
        public MaterialRatesUpdateDto? material_rates { get; set; }

        // Nhóm cấu hình hao hụt.
        public WasteRulesUpdateDto? waste_rules { get; set; }

        // Nhóm cấu hình tham số hệ thống (VAT, rush, số ngày SX mặc định...).
        public SystemParametersUpdateDto? system_parameters { get; set; }

        // Nhóm cấu hình công đoạn sản xuất.
        public ProcessCostsUpdateDto? process_costs { get; set; }

        // Nhóm cấu hình chi phí thiết kế.
        public DesignUpdateDto? design { get; set; }

        // Nhóm cấu hình giá bản kẽm theo khổ.
        public PlatePricesUpdateDto? plate_prices { get; set; }

        // Nhóm cấu hình thanh toán.
        public PaymentTermsUpdateDto? payment_terms { get; set; }

        // Nhóm cấu hình lập lịch sản xuất / giờ làm việc.
        public PlanningUpdateDto? planning { get; set; }

        // Nhóm cấu hình thanh toán khi giao hàng.
        public DeliveryPaymentUpdateDto? delivery_payment { get; set; }
    }

    public class DeliveryPaymentUpdateDto
    {
        public bool? require_remaining_before_delivery { get; set; }
    }

    public class MaterialPricesUpdateDto
    {
        /// <summary>Giá mực in / kg.</summary>
        public decimal? ink_price_per_kg { get; set; }

        /// <summary>Giá keo phủ nước / kg.</summary>
        public decimal? coating_glue_keo_nuoc_per_kg { get; set; }

        /// <summary>Giá keo phủ dầu / kg.</summary>
        public decimal? coating_glue_keo_dau_per_kg { get; set; }

        /// <summary>Giá keo bồi / kg.</summary>
        public decimal? mounting_glue_per_kg { get; set; }

        /// <summary>Giá màng cán / kg.</summary>
        public decimal? lamination_per_kg { get; set; }
    }

    public class MaterialRatesUpdateDto
    {
        /// <summary>Định mức mực gạch nội địa.</summary>
        public decimal? ink_rate_gach_noi_dia { get; set; }

        /// <summary>Định mức mực gạch XK đơn giản.</summary>
        public decimal? ink_rate_gach_xk_don_gian { get; set; }

        /// <summary>Định mức mực hộp màu.</summary>
        public decimal? ink_rate_hop_mau { get; set; }

        /// <summary>Định mức mực gạch nhiều màu.</summary>
        public decimal? ink_rate_gach_nhieu_mau { get; set; }

        /// <summary>Định mức keo phủ nước.</summary>
        public decimal? coating_glue_rate_keo_nuoc { get; set; }

        /// <summary>Định mức keo phủ dầu.</summary>
        public decimal? coating_glue_rate_keo_dau { get; set; }

        /// <summary>Định mức keo bồi.</summary>
        public decimal? mounting_glue_rate { get; set; }

        /// <summary>Định mức màng cán 12 mic.</summary>
        public decimal? lamination_rate_12mic { get; set; }
    }

    public class WasteRulesUpdateDto
    {
        /// <summary>Hao hụt công in.</summary>
        public PrintingWasteUpdateDto? printing { get; set; }

        /// <summary>Hao hụt công bế.</summary>
        public StepWasteSimpleUpdateDto? die_cutting { get; set; }

        /// <summary>Hao hụt công bồi.</summary>
        public StepWasteSimpleUpdateDto? mounting { get; set; }

        /// <summary>Hao hụt công phủ.</summary>
        public CoatingWasteUpdateDto? coating { get; set; }

        /// <summary>
        /// Hao hụt công cán màng.
        /// Lưu ý: code hiện tại chỉ dùng lt_10000 và ge_10000.
        /// </summary>
        public LaminationWasteUpdateDto? lamination { get; set; }

        /// <summary>Hao hụt công dán.</summary>
        public GluingWasteUpdateDto? gluing { get; set; }
    }

    public class PrintingWasteUpdateDto
    {
        /// <summary>Hao hụt in theo mỗi bản kẽm.</summary>
        public int? per_plate { get; set; }

        /// <summary>Hao hụt in mặc định.</summary>
        [JsonPropertyName("default")]
        public int? default_value { get; set; }

        /// <summary>Hao hụt in theo loại sản phẩm.</summary>
        public PrintingByProductTypeUpdateDto? by_product_type { get; set; }
    }

    public class PrintingByProductTypeUpdateDto
    {
        public int? GACH_1MAU { get; set; }
        public int? GACH_XUAT_KHAU_DON_GIAN { get; set; }
        public int? GACH_XUAT_KHAU_TERACON { get; set; }
        public int? GACH_NOI_DIA_4SP { get; set; }
        public int? GACH_NOI_DIA_6SP { get; set; }
        public int? HOP_MAU_1LUOT_DON_GIAN { get; set; }
        public int? HOP_MAU_1LUOT_THUONG { get; set; }
        public int? HOP_MAU_1LUOT_KHO { get; set; }
        public int? HOP_MAU_AQUA_DOI { get; set; }
        public int? HOP_MAU_2LUOT { get; set; }
    }

    public class StepWasteSimpleUpdateDto
    {
        public int? lt_5000 { get; set; }
        public int? lt_20000 { get; set; }
        public int? ge_20000 { get; set; }
    }

    public class CoatingWasteUpdateDto
    {
        public int? keo_nuoc { get; set; }
        public int? keo_dau_lt_10000 { get; set; }
        public int? keo_dau_ge_10000 { get; set; }
    }

    public class LaminationWasteUpdateDto
    {
        public int? lt_10000 { get; set; }
        public int? ge_10000 { get; set; }
    }

    public class GluingWasteUpdateDto
    {
        public int? lt_100 { get; set; }
        public int? lt_500 { get; set; }
        public int? lt_2000 { get; set; }
        public int? ge_2000 { get; set; }
    }

    public class SystemParametersUpdateDto
    {
        /// <summary>Số ngày sản xuất mặc định.</summary>
        public int? default_production_days { get; set; }

        /// <summary>Ngưỡng ngày gấp.</summary>
        public int? rush_threshold_days { get; set; }

        /// <summary>VAT %.</summary>
        public decimal? vat_percent { get; set; }

        /// <summary>Phụ phí rush nếu giao sớm 1 ngày.</summary>
        public decimal? rush_percent_day_1 { get; set; }

        /// <summary>Phụ phí rush nếu giao sớm 2 ngày.</summary>
        public decimal? rush_percent_day_2 { get; set; }

        /// <summary>Phụ phí rush nếu giao sớm 3 ngày.</summary>
        public decimal? rush_percent_day_3 { get; set; }

        /// <summary>Phụ phí rush nếu giao sớm 4 ngày.</summary>
        public decimal? rush_percent_day_4 { get; set; }
    }

    public class ProcessCostsUpdateDto
    {
        public ProcessCostItemUpdateDto? IN { get; set; }
        public ProcessCostItemUpdateDto? PHU { get; set; }
        public ProcessCostItemUpdateDto? CAN { get; set; }
        public ProcessCostItemUpdateDto? BOI { get; set; }
        public ProcessCostItemUpdateDto? BE { get; set; }
        public ProcessCostItemUpdateDto? RALO { get; set; }
        public ProcessCostItemUpdateDto? DAN { get; set; }
        public ProcessCostItemUpdateDto? DUT { get; set; }
        public ProcessCostItemUpdateDto? CAT { get; set; }
    }

    public class ProcessCostItemUpdateDto
    {
        /// <summary>Đơn giá công đoạn.</summary>
        public decimal? unit_price { get; set; }

        /// <summary>Đơn vị tính của công đoạn (m2/tờ/sp...).</summary>
        public string? unit { get; set; }
    }

    public class DesignUpdateDto
    {
        /// <summary>Chi phí thiết kế mặc định.</summary>
        public decimal? default_design_cost { get; set; }
    }

    public class PlatePricesUpdateDto
    {
        public PlatePriceItemUpdateDto? SMALL_37X45 { get; set; }
        public PlatePriceItemUpdateDto? SMALL_40X51 { get; set; }
        public PlatePriceItemUpdateDto? SMALL_45X55 { get; set; }
        public PlatePriceItemUpdateDto? MEDIUM_55X65 { get; set; }
        public PlatePriceItemUpdateDto? MEDIUM_60_5X74_5 { get; set; }
        public PlatePriceItemUpdateDto? MEDIUM_79X60 { get; set; }
        public PlatePriceItemUpdateDto? LARGE_79X103 { get; set; }
        public PlatePriceItemUpdateDto? LARGE_80X103 { get; set; }
        public PlatePriceItemUpdateDto? XLARGE_114X145 { get; set; }
        public PlatePriceItemUpdateDto? XLARGE_132X163 { get; set; }
    }

    public class PlatePriceItemUpdateDto
    {
        /// <summary>Giá mỗi bản kẽm.</summary>
        public decimal? price_per_plate { get; set; }

        /// <summary>Text kích thước hiển thị cho FE/BE (ví dụ: 37 x 45 cm).</summary>
        public string? size_text { get; set; }
    }

    public class PaymentTermsUpdateDto
    {
        /// <summary>
        /// % đặt cọc. Backend sẽ tự tính remaining_percent = 100 - deposit_percent.
        /// FE không cần gửi remaining_percent.
        /// </summary>
        public decimal? deposit_percent { get; set; }
    }

    public class PlanningUpdateDto
    {
        /// <summary>Số giờ chờ tối thiểu trước khi bắt đầu kế hoạch.</summary>
        public decimal? min_start_wait_hours { get; set; }

        /// <summary>Giờ bắt đầu làm việc. Format HH:mm.</summary>
        public string? work_start_time { get; set; }

        /// <summary>Giờ bắt đầu nghỉ trưa. Format HH:mm.</summary>
        public string? break_start_time { get; set; }

        /// <summary>Giờ kết thúc nghỉ trưa. Format HH:mm.</summary>
        public string? break_end_time { get; set; }

        /// <summary>Giờ kết thúc làm việc. Format HH:mm.</summary>
        public string? work_end_time { get; set; }
    }
}
