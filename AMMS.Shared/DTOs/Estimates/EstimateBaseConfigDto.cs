namespace AMMS.Shared.DTOs.Estimates
{
    public class EstimateBaseConfigDto
    {
        public MaterialPriceConfig MaterialPrices { get; set; } = null!;
        public MaterialRateConfig MaterialRates { get; set; } = null!;
        public WasteRuleConfig WasteRules { get; set; } = null!;
        public SystemConfig SystemParameters { get; set; } = null!;
        public ProcessCostConfig ProcessCosts { get; set; } = null!;
        public DesignConfig Design { get; set; } = null!;
        public PlatePriceConfig PlatePrices { get; set; } = null!;
        public PaymentTermsConfig PaymentTerms { get; set; } = null!;
        public PlanningConfig Planning { get; set; } = null!;
        public DeliveryPaymentConfig DeliveryPayment { get; set; } = null!;
    }

    public class DeliveryPaymentConfig
    {
        public bool require_remaining_before_delivery { get; set; } = true;
    }

    public class PlanningConfig
    {
        public decimal min_start_wait_hours { get; set; } = 6;
        public string work_start_time { get; set; } = "08:00";
        public string break_start_time { get; set; } = "12:00";
        public string break_end_time { get; set; } = "13:00";
        public string work_end_time { get; set; } = "17:00";
    }

    public class PaymentTermsConfig
    {
        public decimal deposit_percent { get; set; }
        public decimal remaining_percent { get; set; }
    }

    public class PlatePriceConfig
    {
        public List<PlatePriceItemDto> items { get; set; } = new();
        public List<PlatePriceItemDto> small { get; set; } = new();
        public List<PlatePriceItemDto> medium { get; set; } = new();
        public List<PlatePriceItemDto> large { get; set; } = new();
        public List<PlatePriceItemDto> xlarge { get; set; } = new();
    }

    public class PlatePriceItemDto
    {
        public string key { get; set; } = "";
        public string category { get; set; } = "";
        public string category_text { get; set; } = "";
        public string size_text { get; set; } = "";
        public decimal width_cm { get; set; }
        public decimal height_cm { get; set; }
        public decimal price_per_plate { get; set; }
    }

    public class MaterialPriceConfig
    {
        public decimal ink_price_per_kg { get; set; }
        public decimal coating_glue_keo_nuoc_per_kg { get; set; }
        public decimal coating_glue_keo_dau_per_kg { get; set; }
        public decimal mounting_glue_per_kg { get; set; }
        public decimal lamination_per_kg { get; set; }
    }

    public class MaterialRateConfig
    {
        public decimal ink_rate_gach_noi_dia { get; set; }
        public decimal ink_rate_gach_xk_don_gian { get; set; }
        public decimal ink_rate_hop_mau { get; set; }
        public decimal ink_rate_gach_nhieu_mau { get; set; }
        public decimal coating_glue_rate_keo_nuoc { get; set; }
        public decimal coating_glue_rate_keo_dau { get; set; }
        public decimal mounting_glue_rate { get; set; }
        public decimal lamination_rate_12mic { get; set; }
    }

    public class WasteRuleConfig
    {
        public PrintingWasteConfig Printing { get; set; } = null!;
        public StepWasteSimpleConfig DieCutting { get; set; } = null!;
        public StepWasteSimpleConfig Mounting { get; set; } = null!;
        public CoatingWasteConfig Coating { get; set; } = null!;
        public LaminationWasteConfig Lamination { get; set; } = null!;
        public GluingWasteConfig Gluing { get; set; } = null!;
    }

    public class LaminationWasteConfig
    {
        public int lt_10000 { get; set; }
        public int ge_10000 { get; set; }
    }

    public class PrintingWasteConfig
    {
        public Dictionary<string, int> by_product_type { get; set; } = new();
        public int per_plate { get; set; }
        public int @default { get; set; }
    }

    public class StepWasteSimpleConfig
    {
        public int lt_5000 { get; set; }
        public int lt_20000 { get; set; }
        public int ge_20000 { get; set; }
    }

    public class CoatingWasteConfig
    {
        public int keo_nuoc { get; set; }
        public int keo_dau_lt_10000 { get; set; }
        public int keo_dau_ge_10000 { get; set; }
    }

    public class GluingWasteConfig
    {
        public int lt_100 { get; set; }
        public int lt_500 { get; set; }
        public int lt_2000 { get; set; }
        public int ge_2000 { get; set; }
    }

    public class SystemConfig
    {
        public int default_production_days { get; set; }
        public int rush_threshold_days { get; set; }
        public decimal vat_percent { get; set; }
        public decimal min_start_wait_hours { get; set; } = 6;
        public Dictionary<int, decimal> rush_percent_by_days_early { get; set; } = new();
    }

    public class ProcessCostConfig
    {
        public Dictionary<string, ProcessCostItemConfig> by_process { get; set; } = new();
    }

    public class ProcessCostItemConfig
    {
        public decimal unit_price { get; set; }
        public string unit { get; set; } = "";
        public string note { get; set; } = "";
    }

    public class DesignConfig
    {
        public decimal default_design_cost { get; set; }
    }
}