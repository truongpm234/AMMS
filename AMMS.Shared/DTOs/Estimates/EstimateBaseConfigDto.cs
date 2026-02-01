using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
        // Ink
        public decimal ink_rate_gach_noi_dia { get; set; }
        public decimal ink_rate_gach_xk_don_gian { get; set; }
        public decimal ink_rate_hop_mau { get; set; }
        public decimal ink_rate_gach_nhieu_mau { get; set; }

        // Coating glue
        public decimal coating_glue_rate_keo_nuoc { get; set; }
        public decimal coating_glue_rate_keo_dau { get; set; }

        // Mounting glue
        public decimal mounting_glue_rate { get; set; }

        // Lamination
        public decimal lamination_rate_12mic { get; set; }
    }

    public class WasteRuleConfig
    {
        public PrintingWasteConfig Printing { get; set; } = null!;
        public StepWasteSimpleConfig DieCutting { get; set; } = null!;
        public StepWasteSimpleConfig Mounting { get; set; } = null!;
        public CoatingWasteConfig Coating { get; set; } = null!;
        public StepWasteSimpleConfig Lamination { get; set; } = null!;
        public GluingWasteConfig Gluing { get; set; } = null!;
    }

    public class PrintingWasteConfig
    {
        public Dictionary<string, int> by_product_type { get; set; } = new();
        public int per_plate { get; set; }
        public int @default { get; set; }
    }

    public class StepWasteSimpleConfig
    {
        // theo sheets_base
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
        // theo quantity (số hộp)
        public int lt_100 { get; set; }
        public int lt_500 { get; set; }
        public int lt_2000 { get; set; }
        public int ge_2000 { get; set; }
    }

    public class SystemConfig
    {
        public decimal overhead_percent { get; set; }
        public int default_production_days { get; set; }
        public int rush_threshold_days { get; set; }
        public decimal vat_percent { get; set; }
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

