using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using AMMS.Application.Rules;
using AMMS.Shared.DTOs.Estimates;

namespace AMMS.Application.Helpers
{
    public static class EstimateConfigBuilder
    {
        public static EstimateBaseConfigDto Build()
        {
            return new EstimateBaseConfigDto
            {
                MaterialPrices = new MaterialPriceConfig
                {
                    ink_price_per_kg = MaterialPrices.INK_PRICE_PER_KG,
                    coating_glue_keo_nuoc_per_kg = MaterialPrices.COATING_GLUE_KEO_NUOC_PER_KG,
                    coating_glue_keo_dau_per_kg = MaterialPrices.COATING_GLUE_KEO_DAU_PER_KG,
                    mounting_glue_per_kg = MaterialPrices.MOUNTING_GLUE_PER_KG,
                    lamination_per_kg = MaterialPrices.LAMINATION_PER_KG
                },
                MaterialRates = new MaterialRateConfig
                {
                    ink_rate_gach_noi_dia = MaterialRates.InkRates.GACH_NOI_DIA,
                    ink_rate_gach_xk_don_gian = MaterialRates.InkRates.GACH_XUAT_KHAU_DON_GIAN,
                    ink_rate_hop_mau = MaterialRates.InkRates.HOP_MAU,
                    ink_rate_gach_nhieu_mau = MaterialRates.InkRates.GACH_NHIEU_MAU,

                    coating_glue_rate_keo_nuoc = MaterialRates.CoatingGlueRates.KEO_NUOC,
                    coating_glue_rate_keo_dau = MaterialRates.CoatingGlueRates.KEO_DAU,

                    mounting_glue_rate = MaterialRates.MountingGlueRates.RATE,
                    lamination_rate_12mic = MaterialRates.LaminationRates.RATE_12MIC
                },
                WasteRules = BuildWasteRules(),
                SystemParameters = new SystemConfig
                {
                    overhead_percent = SystemParameters.OVERHEAD_PERCENT,
                    default_production_days = SystemParameters.DEFAULT_PRODUCTION_DAYS,
                    rush_threshold_days = SystemParameters.RUSH_THRESHOLD_DAYS,
                    rush_percent_by_days_early = new Dictionary<int, decimal>
                    {
                        { 1, 5m },
                        { 2, 20m },
                        { 3, 20m },
                        { 4, 40m } // FE có thể hiểu: >=4 dùng 40%
                    }
                },
                ProcessCosts = BuildProcessCosts(),
                Design = new DesignConfig
                {
                    default_design_cost = 200_000m
                }
            };
        }

        private static WasteRuleConfig BuildWasteRules()
        {
            var printing = new PrintingWasteConfig
            {
                per_plate = WasteCalculationRules.PrintingWaste.PER_PLATE,
                @default = 200 // default của GetBaseWaste khi không match enum
            };

            // map cụ thể các mã product type
            printing.by_product_type["GACH_1MAU"] = WasteCalculationRules.PrintingWaste.GACH_1MAU;
            printing.by_product_type["GACH_XUAT_KHAU_DON_GIAN"] = WasteCalculationRules.PrintingWaste.GACH_XUAT_KHAU_DON_GIAN;
            printing.by_product_type["GACH_XUAT_KHAU_TERACON"] = WasteCalculationRules.PrintingWaste.GACH_XUAT_KHAU_TERACON;
            printing.by_product_type["GACH_NOI_DIA_4SP"] = WasteCalculationRules.PrintingWaste.GACH_NOI_DIA_4SP;
            printing.by_product_type["GACH_NOI_DIA_6SP"] = WasteCalculationRules.PrintingWaste.GACH_NOI_DIA_6SP;

            printing.by_product_type["HOP_MAU_1LUOT_DON_GIAN"] = WasteCalculationRules.PrintingWaste.HOP_MAU_1LUOT_DON_GIAN;
            printing.by_product_type["HOP_MAU_1LUOT_THUONG"] = WasteCalculationRules.PrintingWaste.HOP_MAU_1LUOT_THUONG;
            printing.by_product_type["HOP_MAU_1LUOT_KHO"] = WasteCalculationRules.PrintingWaste.HOP_MAU_1LUOT_KHO;
            printing.by_product_type["HOP_MAU_AQUA_DOI"] = WasteCalculationRules.PrintingWaste.HOP_MAU_AQUA_DOI;
            printing.by_product_type["HOP_MAU_2LUOT"] = WasteCalculationRules.PrintingWaste.HOP_MAU_2LUOT_1
                                                       + WasteCalculationRules.PrintingWaste.HOP_MAU_2LUOT_2;

            var simpleDie = new StepWasteSimpleConfig
            {
                lt_5000 = 20,
                lt_20000 = 30,
                ge_20000 = 40
            };

            var simpleMount = new StepWasteSimpleConfig
            {
                lt_5000 = 20,
                lt_20000 = 30,
                ge_20000 = 40
            };

            var lamination = new StepWasteSimpleConfig
            {
                lt_5000 = 20,    // < 10.000
                lt_20000 = 30,   // >= 10.000 (xài field này)
                ge_20000 = 30
            };

            var coating = new CoatingWasteConfig
            {
                keo_nuoc = 0,
                keo_dau_lt_10000 = 20,
                keo_dau_ge_10000 = 30
            };

            var gluing = new GluingWasteConfig
            {
                lt_100 = 10,
                lt_500 = 15,
                lt_2000 = 20,
                ge_2000 = 25
            };

            return new WasteRuleConfig
            {
                Printing = printing,
                DieCutting = simpleDie,
                Mounting = simpleMount,
                Coating = coating,
                Lamination = lamination,
                Gluing = gluing
            };
        }

        private static ProcessCostConfig BuildProcessCosts()
        {
            var result = new ProcessCostConfig
            {
                by_process = new Dictionary<string, ProcessCostItemConfig>()
            };

            foreach (var p in System.Enum.GetValues<Shared.DTOs.Enums.ProcessType>())
            {
                var (unitPrice, unit, note) = ProcessCostRules.GetRate(p);
                result.by_process[p.ToString()] = new ProcessCostItemConfig
                {
                    unit_price = unitPrice,
                    unit = unit,
                    note = note
                };
            }

            return result;
        }
    }
}
