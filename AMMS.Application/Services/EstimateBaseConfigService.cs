using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Application.Interfaces;
using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Estimates;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Services
{
    public class EstimateBaseConfigService : IEstimateBaseConfigService
    {
        private readonly AppDbContext _db;

        public EstimateBaseConfigService(AppDbContext db)
        {
            _db = db;
        }

        public async Task<EstimateBaseConfigDto> GetAsync(CancellationToken ct = default)
        {
            var rows = await _db.estimate_config
                .AsNoTracking()
                .ToListAsync(ct);

            var lookup = rows
                .GroupBy(x => x.config_group)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(x => x.config_key, x => x, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase
                );

            decimal GetNum(string group, string key)
            {
                if (!lookup.TryGetValue(group, out var g) || !g.TryGetValue(key, out var row) || row.value_num is null)
                    throw new InvalidOperationException($"Missing config: [{group}].[{key}] (value_num is null/missing)");
                return row.value_num.Value;
            }

            int GetInt(string group, string key)
                => (int)Math.Round(GetNum(group, key), 0);

            var processRules = await _db.Set<process_cost_rule>()
                .AsNoTracking()
                .ToListAsync(ct);

            var dto = new EstimateBaseConfigDto
            {
                MaterialPrices = new MaterialPriceConfig
                {
                    ink_price_per_kg = GetNum("materialPrices", "ink_price_per_kg"),
                    coating_glue_keo_nuoc_per_kg = GetNum("materialPrices", "coating_glue_keo_nuoc_per_kg"),
                    coating_glue_keo_dau_per_kg = GetNum("materialPrices", "coating_glue_keo_dau_per_kg"),
                    mounting_glue_per_kg = GetNum("materialPrices", "mounting_glue_per_kg"),
                    lamination_per_kg = GetNum("materialPrices", "lamination_per_kg"),
                },
                MaterialRates = new MaterialRateConfig
                {
                    ink_rate_gach_noi_dia = GetNum("materialRates", "ink_rate_gach_noi_dia"),
                    ink_rate_gach_xk_don_gian = GetNum("materialRates", "ink_rate_gach_xk_don_gian"),
                    ink_rate_hop_mau = GetNum("materialRates", "ink_rate_hop_mau"),
                    ink_rate_gach_nhieu_mau = GetNum("materialRates", "ink_rate_gach_nhieu_mau"),

                    coating_glue_rate_keo_nuoc = GetNum("materialRates", "coating_glue_rate_keo_nuoc"),
                    coating_glue_rate_keo_dau = GetNum("materialRates", "coating_glue_rate_keo_dau"),

                    mounting_glue_rate = GetNum("materialRates", "mounting_glue_rate"),
                    lamination_rate_12mic = GetNum("materialRates", "lamination_rate_12mic"),
                },
                WasteRules = new WasteRuleConfig
                {
                    Printing = BuildPrintingWaste(lookup),
                    DieCutting = new StepWasteSimpleConfig
                    {
                        lt_5000 = GetInt("wasteRules.dieCutting", "lt_5000"),
                        lt_20000 = GetInt("wasteRules.dieCutting", "lt_20000"),
                        ge_20000 = GetInt("wasteRules.dieCutting", "ge_20000"),
                    },
                    Mounting = new StepWasteSimpleConfig
                    {
                        lt_5000 = GetInt("wasteRules.mounting", "lt_5000"),
                        lt_20000 = GetInt("wasteRules.mounting", "lt_20000"),
                        ge_20000 = GetInt("wasteRules.mounting", "ge_20000"),
                    },
                    Coating = new CoatingWasteConfig
                    {
                        keo_nuoc = GetInt("wasteRules.coating", "keo_nuoc"),
                        keo_dau_lt_10000 = GetInt("wasteRules.coating", "keo_dau_lt_10000"),
                        keo_dau_ge_10000 = GetInt("wasteRules.coating", "keo_dau_ge_10000"),
                    },
                    Lamination = new StepWasteSimpleConfig
                    {
                        lt_5000 = GetInt("wasteRules.lamination", "lt_5000"),
                        lt_20000 = GetInt("wasteRules.lamination", "lt_20000"),
                        ge_20000 = GetInt("wasteRules.lamination", "ge_20000"),
                    },
                    Gluing = new GluingWasteConfig
                    {
                        lt_100 = GetInt("wasteRules.gluing", "lt_100"),
                        lt_500 = GetInt("wasteRules.gluing", "lt_500"),
                        lt_2000 = GetInt("wasteRules.gluing", "lt_2000"),
                        ge_2000 = GetInt("wasteRules.gluing", "ge_2000"),
                    }
                },
                SystemParameters = new SystemConfig
                {
                    overhead_percent = GetNum("systemParameters", "overhead_percent"),
                    default_production_days = GetInt("systemParameters", "default_production_days"),
                    rush_threshold_days = GetInt("systemParameters", "rush_threshold_days"),
                    vat_percent = GetNum("systemParameters", "vat_percent"),
                    rush_percent_by_days_early = BuildRushMap(lookup),
                },
                ProcessCosts = new ProcessCostConfig
                {
                    by_process = processRules.ToDictionary(
                        x => x.process_code,
                        x => new ProcessCostItemConfig
                        {
                            unit_price = x.unit_price,
                            unit = x.unit,
                            note = x.note ?? ""
                        },
                        StringComparer.OrdinalIgnoreCase
                    )
                },
                Design = new DesignConfig
                {
                    default_design_cost = GetNum("design", "default_design_cost")
                }
            };

            return dto;
        }

        private static PrintingWasteConfig BuildPrintingWaste(
            Dictionary<string, Dictionary<string, AMMS.Infrastructure.Entities.estimate_config>> lookup)
        {
            decimal RequireNum(string group, string key)
            {
                if (!lookup.TryGetValue(group, out var g) || !g.TryGetValue(key, out var row) || row.value_num is null)
                    throw new InvalidOperationException($"Missing config: [{group}].[{key}]");
                return row.value_num.Value;
            }

            var cfg = new PrintingWasteConfig
            {
                per_plate = (int)RequireNum("wasteRules.printing", "per_plate"),
                @default = (int)RequireNum("wasteRules.printing", "default"),
                by_product_type = new Dictionary<string, int>()
            };

            if (lookup.TryGetValue("wasteRules.printing.by_product_type", out var map))
            {
                foreach (var kv in map)
                {
                    if (kv.Value.value_num is null) continue;
                    cfg.by_product_type[kv.Key] = (int)Math.Round(kv.Value.value_num.Value, 0);
                }
            }

            return cfg;
        }

        private static Dictionary<int, decimal> BuildRushMap(
            Dictionary<string, Dictionary<string, AMMS.Infrastructure.Entities.estimate_config>> lookup)
        {
            var result = new Dictionary<int, decimal>();

            if (!lookup.TryGetValue("systemParameters.rush_percent_by_days_early", out var map))
                return result;

            foreach (var kv in map)
            {
                if (!int.TryParse(kv.Key, out var day)) continue;
                if (kv.Value.value_num is null) continue;
                result[day] = kv.Value.value_num.Value;
            }

            return result;
        }
    }
}

