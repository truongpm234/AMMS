using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AMMS.Infrastructure.Repositories
{
    public class BaseConfigRepository : IBaseConfigRepository
    {
        private readonly AppDbContext _db;

        public BaseConfigRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<EstimateBaseConfigDto> GetAsync(CancellationToken ct)
        {
            var rows = await _db.Set<estimate_config>()
                .AsNoTracking()
                .ToListAsync(ct);

            var planning = MapPlanning(rows);

            var dto = new EstimateBaseConfigDto
            {
                MaterialPrices = MapMaterialPrices(rows),
                MaterialRates = MapMaterialRates(rows),
                WasteRules = MapWasteRules(rows),
                SystemParameters = MapSystemParameters(rows, planning),
                ProcessCosts = MapProcessCosts(rows),
                Design = MapDesign(rows),
                PlatePrices = MapPlatePrices(rows),
                PaymentTerms = MapPaymentTerms(rows),
                Planning = planning,
                DeliveryPayment = MapDeliveryPayment(rows)

            };

            return dto;
        }

        private static DeliveryPaymentConfig MapDeliveryPayment(List<estimate_config> rows)
        {
            bool GetBool(string key, bool fallback)
            {
                var value = rows
                    .Where(x => x.config_group == "deliveryPayment" && x.config_key == key)
                    .OrderByDescending(x => x.updated_at)
                    .Select(x => x.value_bool)
                    .FirstOrDefault();

                return value ?? fallback;
            }

            return new DeliveryPaymentConfig
            {
                require_remaining_before_delivery =
                    GetBool("require_remaining_before_delivery", true)
            };
        }

        private static PlanningConfig MapPlanning(List<estimate_config> rows)
        {
            decimal GetNum(string key, decimal fallback) =>
                rows.FirstOrDefault(x => x.config_group == "planning" && x.config_key == key)?.value_num ?? fallback;

            string GetText(string key, string fallback)
            {
                var raw = rows
                    .Where(x => x.config_group == "planning" && x.config_key == key)
                    .OrderByDescending(x => x.updated_at)
                    .Select(x => x.value_text)
                    .FirstOrDefault();

                return NormalizeTimeText(raw, fallback);
            }

            return new PlanningConfig
            {
                min_start_wait_hours = GetNum("min_start_wait_hours", 6m),
                work_start_time = GetText("work_start_time", "08:00"),
                break_start_time = GetText("break_start_time", "12:00"),
                break_end_time = GetText("break_end_time", "13:00"),
                work_end_time = GetText("work_end_time", "17:00")
            };
        }
        private static string NormalizeTimeText(string? raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (TimeSpan.TryParse(raw, out var ts))
                return $"{ts.Hours:D2}:{ts.Minutes:D2}";

            return fallback;
        }

        public async Task<PaymentTermsConfig> GetPaymentTermsAsync(CancellationToken ct)
        {
            var rows = await _db.Set<estimate_config>()
                .AsNoTracking()
                .Where(x => x.config_group == "paymentTerms")
                .ToListAsync(ct);

            return MapPaymentTerms(rows);
        }

        private static PaymentTermsConfig MapPaymentTerms(List<estimate_config> rows)
        {
            decimal GetNum(string key, decimal fallback) =>
                rows.FirstOrDefault(x => x.config_group == "paymentTerms" && x.config_key == key)?.value_num
                ?? fallback;

            var deposit = GetNum("deposit_percent", 30m);
            var remaining = GetNum("remaining_percent", 70m);

            if (deposit < 0) deposit = 0;
            if (deposit > 100) deposit = 100;

            // nếu DB chưa đồng bộ hết thì vẫn tự cân ở tầng app
            remaining = 100m - deposit;

            return new PaymentTermsConfig
            {
                deposit_percent = deposit,
                remaining_percent = remaining
            };
        }

        private static PlatePriceConfig MapPlatePrices(List<estimate_config> rows)
        {
            var items = rows
                .Where(x => x.config_group == "platePrices")
                .OrderBy(x => GetPlateSortOrder(x.config_key))
                .ThenBy(x => x.config_key)
                .Select(x => new PlatePriceItemDto
                {
                    key = x.config_key,
                    category = GetPlateCategory(x.config_key),
                    category_text = GetPlateCategoryText(x.config_key),
                    size_text = x.value_text ?? "",
                    width_cm = GetPlateWidth(x.config_key),
                    height_cm = GetPlateHeight(x.config_key),
                    price_per_plate = x.value_num ?? 0
                })
                .ToList();

            return new PlatePriceConfig
            {
                items = items,
                small = items.Where(x => x.category == "small").ToList(),
                medium = items.Where(x => x.category == "medium").ToList(),
                large = items.Where(x => x.category == "large").ToList(),
                xlarge = items.Where(x => x.category == "xlarge").ToList()
            };
        }

        private static MaterialPriceConfig MapMaterialPrices(List<estimate_config> rows)
        {
            decimal GetNum(string group, string key) =>
                rows.FirstOrDefault(x => x.config_group == group && x.config_key == key)?.value_num ?? 0;

            return new MaterialPriceConfig
            {
                ink_price_per_kg = GetNum("materialPrices", "ink_price_per_kg"),
                coating_glue_keo_nuoc_per_kg = GetNum("materialPrices", "coating_glue_keo_nuoc_per_kg"),
                coating_glue_keo_dau_per_kg = GetNum("materialPrices", "coating_glue_keo_dau_per_kg"),
                mounting_glue_per_kg = GetNum("materialPrices", "mounting_glue_per_kg"),
                lamination_per_kg = GetNum("materialPrices", "lamination_per_kg")
            };
        }

        private static MaterialRateConfig MapMaterialRates(List<estimate_config> rows)
        {
            decimal GetNum(string group, string key) =>
                rows.FirstOrDefault(x => x.config_group == group && x.config_key == key)?.value_num ?? 0;

            return new MaterialRateConfig
            {
                ink_rate_gach_noi_dia = GetNum("materialRates", "ink_rate_gach_noi_dia"),
                ink_rate_gach_xk_don_gian = GetNum("materialRates", "ink_rate_gach_xk_don_gian"),
                ink_rate_hop_mau = GetNum("materialRates", "ink_rate_hop_mau"),
                ink_rate_gach_nhieu_mau = GetNum("materialRates", "ink_rate_gach_nhieu_mau"),
                coating_glue_rate_keo_nuoc = GetNum("materialRates", "coating_glue_rate_keo_nuoc"),
                coating_glue_rate_keo_dau = GetNum("materialRates", "coating_glue_rate_keo_dau"),
                mounting_glue_rate = GetNum("materialRates", "mounting_glue_rate"),
                lamination_rate_12mic = GetNum("materialRates", "lamination_rate_12mic")
            };
        }

        private static WasteRuleConfig MapWasteRules(List<estimate_config> rows)
        {
            int GetInt(string group, string key) =>
                (int)(rows.FirstOrDefault(x => x.config_group == group && x.config_key == key)?.value_num ?? 0);

            var printingByProductType = rows
                .Where(x => x.config_group == "wasteRules.printing.by_product_type")
                .ToDictionary(x => x.config_key, x => (int)(x.value_num ?? 0));

            return new WasteRuleConfig
            {
                Printing = new PrintingWasteConfig
                {
                    by_product_type = printingByProductType,
                    per_plate = GetInt("wasteRules.printing", "per_plate"),
                    @default = GetInt("wasteRules.printing", "default")
                },
                DieCutting = new StepWasteSimpleConfig
                {
                    lt_5000 = GetInt("wasteRules.dieCutting", "lt_5000"),
                    lt_20000 = GetInt("wasteRules.dieCutting", "lt_20000"),
                    ge_20000 = GetInt("wasteRules.dieCutting", "ge_20000")
                },
                Mounting = new StepWasteSimpleConfig
                {
                    lt_5000 = GetInt("wasteRules.mounting", "lt_5000"),
                    lt_20000 = GetInt("wasteRules.mounting", "lt_20000"),
                    ge_20000 = GetInt("wasteRules.mounting", "ge_20000")
                },
                Coating = new CoatingWasteConfig
                {
                    keo_nuoc = GetInt("wasteRules.coating", "keo_nuoc"),
                    keo_dau_lt_10000 = GetInt("wasteRules.coating", "keo_dau_lt_10000"),
                    keo_dau_ge_10000 = GetInt("wasteRules.coating", "keo_dau_ge_10000")
                },
                Lamination = new LaminationWasteConfig
                {
                    lt_10000 = GetInt("wasteRules.lamination", "lt_10000"),
                    ge_10000 = GetInt("wasteRules.lamination", "ge_10000")
                },
                Gluing = new GluingWasteConfig
                {
                    lt_100 = GetInt("wasteRules.gluing", "lt_100"),
                    lt_500 = GetInt("wasteRules.gluing", "lt_500"),
                    lt_2000 = GetInt("wasteRules.gluing", "lt_2000"),
                    ge_2000 = GetInt("wasteRules.gluing", "ge_2000")
                }
            };
        }

        private static SystemConfig MapSystemParameters(List<estimate_config> rows, PlanningConfig planning)
        {
            decimal GetNum(string group, string key) =>
                rows.FirstOrDefault(x => x.config_group == group && x.config_key == key)?.value_num ?? 0;

            var rush = rows
                .Where(x => x.config_group == "systemParameters.rush_percent_by_days_early")
                .ToDictionary(
                    x => int.TryParse(x.config_key, out var day) ? day : 0,
                    x => x.value_num ?? 0
                );

            return new SystemConfig
            {
                default_production_days = (int)GetNum("systemParameters", "default_production_days"),
                rush_threshold_days = (int)GetNum("systemParameters", "rush_threshold_days"),
                vat_percent = GetNum("systemParameters", "vat_percent"),
                min_start_wait_hours = planning.min_start_wait_hours,
                rush_percent_by_days_early = rush
            };
        }

        private static ProcessCostConfig MapProcessCosts(List<estimate_config> rows)
        {
            var byProcess = rows
                .Where(x => x.config_group == "processCosts.by_process")
                .OrderBy(x => x.config_key)
                .ToDictionary(
                    x => x.config_key,
                    x =>
                    {
                        var meta = ParseProcessCostMeta(x.value_json);

                        return new ProcessCostItemConfig
                        {
                            unit_price = x.value_num ?? 0,
                            unit = x.value_text ?? "",
                            note = meta.note
                        };
                    },
                    StringComparer.OrdinalIgnoreCase
                );

            return new ProcessCostConfig
            {
                by_process = byProcess
            };
        }

        private static DesignConfig MapDesign(List<estimate_config> rows)
        {
            decimal GetNum(string group, string key) =>
                rows.FirstOrDefault(x => x.config_group == group && x.config_key == key)?.value_num ?? 0;

            return new DesignConfig
            {
                default_design_cost = GetNum("design", "default_design_cost")
            };
        }

        private static ProcessCostMeta ParseProcessCostMeta(string? valueJson)
        {
            if (string.IsNullOrWhiteSpace(valueJson))
                return new ProcessCostMeta();

            try
            {
                return JsonSerializer.Deserialize<ProcessCostMeta>(valueJson) ?? new ProcessCostMeta();
            }
            catch
            {
                return new ProcessCostMeta();
            }
        }

        private sealed class ProcessCostMeta
        {
            public string process_name { get; set; } = "";
            public string note { get; set; } = "";
        }

        private static string GetPlateCategory(string key)
        {
            if (key.StartsWith("SMALL_", StringComparison.OrdinalIgnoreCase)) return "small";
            if (key.StartsWith("MEDIUM_", StringComparison.OrdinalIgnoreCase)) return "medium";
            if (key.StartsWith("LARGE_", StringComparison.OrdinalIgnoreCase)) return "large";
            if (key.StartsWith("XLARGE_", StringComparison.OrdinalIgnoreCase)) return "xlarge";
            return "";
        }

        private static string GetPlateCategoryText(string key)
        {
            if (key.StartsWith("SMALL_", StringComparison.OrdinalIgnoreCase)) return "Khổ nhỏ";
            if (key.StartsWith("MEDIUM_", StringComparison.OrdinalIgnoreCase)) return "Khổ trung";
            if (key.StartsWith("LARGE_", StringComparison.OrdinalIgnoreCase)) return "Khổ lớn";
            if (key.StartsWith("XLARGE_", StringComparison.OrdinalIgnoreCase)) return "Khổ siêu lớn";
            return "";
        }

        private static int GetPlateSortOrder(string key) => key switch
        {
            "SMALL_37X45" => 1,
            "SMALL_40X51" => 2,
            "SMALL_45X55" => 3,
            "MEDIUM_55X65" => 4,
            "MEDIUM_60_5X74_5" => 5,
            "MEDIUM_79X60" => 6,
            "LARGE_79X103" => 7,
            "LARGE_80X103" => 8,
            "XLARGE_114X145" => 9,
            "XLARGE_132X163" => 10,
            _ => 999
        };

        private static decimal GetPlateWidth(string key) => key switch
        {
            "SMALL_37X45" => 37,
            "SMALL_40X51" => 40,
            "SMALL_45X55" => 45,
            "MEDIUM_55X65" => 55,
            "MEDIUM_60_5X74_5" => 60.5m,
            "MEDIUM_79X60" => 79,
            "LARGE_79X103" => 79,
            "LARGE_80X103" => 80,
            "XLARGE_114X145" => 114,
            "XLARGE_132X163" => 132,
            _ => 0
        };

        private static decimal GetPlateHeight(string key) => key switch
        {
            "SMALL_37X45" => 45,
            "SMALL_40X51" => 51,
            "SMALL_45X55" => 55,
            "MEDIUM_55X65" => 65,
            "MEDIUM_60_5X74_5" => 74.5m,
            "MEDIUM_79X60" => 60,
            "LARGE_79X103" => 103,
            "LARGE_80X103" => 103,
            "XLARGE_114X145" => 145,
            "XLARGE_132X163" => 163,
            _ => 0
        };

        public async Task UpdateAsync(UpdateEstimateBaseConfigRequest dto, CancellationToken ct)
        {
            if (dto == null)
                throw new ArgumentNullException(nameof(dto));

            var now = AppTime.NowVnUnspecified();

            // 1) MATERIAL PRICES
            if (dto.material_prices != null)
            {
                await UpsertNumericIfHasValueAsync("materialPrices", "ink_price_per_kg", dto.material_prices.ink_price_per_kg, now, ct);
                await UpsertNumericIfHasValueAsync("materialPrices", "coating_glue_keo_nuoc_per_kg", dto.material_prices.coating_glue_keo_nuoc_per_kg, now, ct);
                await UpsertNumericIfHasValueAsync("materialPrices", "coating_glue_keo_dau_per_kg", dto.material_prices.coating_glue_keo_dau_per_kg, now, ct);
                await UpsertNumericIfHasValueAsync("materialPrices", "mounting_glue_per_kg", dto.material_prices.mounting_glue_per_kg, now, ct);
                await UpsertNumericIfHasValueAsync("materialPrices", "lamination_per_kg", dto.material_prices.lamination_per_kg, now, ct);
            }

            // 2) MATERIAL RATES
            if (dto.material_rates != null)
            {
                await UpsertNumericIfHasValueAsync("materialRates", "ink_rate_gach_noi_dia", dto.material_rates.ink_rate_gach_noi_dia, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "ink_rate_gach_xk_don_gian", dto.material_rates.ink_rate_gach_xk_don_gian, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "ink_rate_hop_mau", dto.material_rates.ink_rate_hop_mau, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "ink_rate_gach_nhieu_mau", dto.material_rates.ink_rate_gach_nhieu_mau, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "coating_glue_rate_keo_nuoc", dto.material_rates.coating_glue_rate_keo_nuoc, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "coating_glue_rate_keo_dau", dto.material_rates.coating_glue_rate_keo_dau, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "mounting_glue_rate", dto.material_rates.mounting_glue_rate, now, ct);
                await UpsertNumericIfHasValueAsync("materialRates", "lamination_rate_12mic", dto.material_rates.lamination_rate_12mic, now, ct);
            }

            // 3) WASTE RULES
            if (dto.waste_rules?.printing != null)
            {
                await UpsertIntegerIfHasValueAsync("wasteRules.printing", "per_plate", dto.waste_rules.printing.per_plate, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.printing", "default", dto.waste_rules.printing.default_value, now, ct);

                var byProductType = dto.waste_rules.printing.by_product_type;
                if (byProductType != null)
                {
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "GACH_1MAU", byProductType.GACH_1MAU, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "GACH_XUAT_KHAU_DON_GIAN", byProductType.GACH_XUAT_KHAU_DON_GIAN, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "GACH_XUAT_KHAU_TERACON", byProductType.GACH_XUAT_KHAU_TERACON, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "GACH_NOI_DIA_4SP", byProductType.GACH_NOI_DIA_4SP, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "GACH_NOI_DIA_6SP", byProductType.GACH_NOI_DIA_6SP, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "HOP_MAU_1LUOT_DON_GIAN", byProductType.HOP_MAU_1LUOT_DON_GIAN, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "HOP_MAU_1LUOT_THUONG", byProductType.HOP_MAU_1LUOT_THUONG, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "HOP_MAU_1LUOT_KHO", byProductType.HOP_MAU_1LUOT_KHO, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "HOP_MAU_AQUA_DOI", byProductType.HOP_MAU_AQUA_DOI, now, ct);
                    await UpsertIntegerIfHasValueAsync("wasteRules.printing.by_product_type", "HOP_MAU_2LUOT", byProductType.HOP_MAU_2LUOT, now, ct);
                }
            }

            if (dto.waste_rules?.die_cutting != null)
            {
                await UpsertIntegerIfHasValueAsync("wasteRules.dieCutting", "lt_5000", dto.waste_rules.die_cutting.lt_5000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.dieCutting", "lt_20000", dto.waste_rules.die_cutting.lt_20000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.dieCutting", "ge_20000", dto.waste_rules.die_cutting.ge_20000, now, ct);
            }

            if (dto.waste_rules?.mounting != null)
            {
                await UpsertIntegerIfHasValueAsync("wasteRules.mounting", "lt_5000", dto.waste_rules.mounting.lt_5000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.mounting", "lt_20000", dto.waste_rules.mounting.lt_20000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.mounting", "ge_20000", dto.waste_rules.mounting.ge_20000, now, ct);
            }

            if (dto.waste_rules?.coating != null)
            {
                await UpsertIntegerIfHasValueAsync("wasteRules.coating", "keo_nuoc", dto.waste_rules.coating.keo_nuoc, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.coating", "keo_dau_lt_10000", dto.waste_rules.coating.keo_dau_lt_10000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.coating", "keo_dau_ge_10000", dto.waste_rules.coating.keo_dau_ge_10000, now, ct);
            }

            if (dto.waste_rules?.lamination != null)
            {
                await UpsertIntegerIfHasValueAsync("wasteRules.lamination", "lt_10000", dto.waste_rules.lamination.lt_10000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.lamination", "ge_10000", dto.waste_rules.lamination.ge_10000, now, ct);
            }

            if (dto.waste_rules?.gluing != null)
            {
                await UpsertIntegerIfHasValueAsync("wasteRules.gluing", "lt_100", dto.waste_rules.gluing.lt_100, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.gluing", "lt_500", dto.waste_rules.gluing.lt_500, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.gluing", "lt_2000", dto.waste_rules.gluing.lt_2000, now, ct);
                await UpsertIntegerIfHasValueAsync("wasteRules.gluing", "ge_2000", dto.waste_rules.gluing.ge_2000, now, ct);
            }

            // 4) SYSTEM PARAMETERS
            if (dto.system_parameters != null)
            {
                await UpsertIntegerIfHasValueAsync("systemParameters", "default_production_days", dto.system_parameters.default_production_days, now, ct);
                await UpsertIntegerIfHasValueAsync("systemParameters", "rush_threshold_days", dto.system_parameters.rush_threshold_days, now, ct);
                await UpsertNumericIfHasValueAsync("systemParameters", "vat_percent", dto.system_parameters.vat_percent, now, ct);

                await UpsertNumericIfHasValueAsync("systemParameters.rush_percent_by_days_early", "1", dto.system_parameters.rush_percent_day_1, now, ct);
                await UpsertNumericIfHasValueAsync("systemParameters.rush_percent_by_days_early", "2", dto.system_parameters.rush_percent_day_2, now, ct);
                await UpsertNumericIfHasValueAsync("systemParameters.rush_percent_by_days_early", "3", dto.system_parameters.rush_percent_day_3, now, ct);
                await UpsertNumericIfHasValueAsync("systemParameters.rush_percent_by_days_early", "4", dto.system_parameters.rush_percent_day_4, now, ct);
            }

            // 5) PROCESS COSTS
            if (dto.process_costs != null)
            {
                await UpsertProcessCostAsync("IN", dto.process_costs.IN, now, ct);
                await UpsertProcessCostAsync("PHU", dto.process_costs.PHU, now, ct);
                await UpsertProcessCostAsync("CAN", dto.process_costs.CAN, now, ct);
                await UpsertProcessCostAsync("BOI", dto.process_costs.BOI, now, ct);
                await UpsertProcessCostAsync("BE", dto.process_costs.BE, now, ct);
                await UpsertProcessCostAsync("RALO", dto.process_costs.RALO, now, ct);
                await UpsertProcessCostAsync("DAN", dto.process_costs.DAN, now, ct);
                await UpsertProcessCostAsync("DUT", dto.process_costs.DUT, now, ct);
                await UpsertProcessCostAsync("CAT", dto.process_costs.CAT, now, ct);
            }

            // 6) DESIGN
            if (dto.design != null)
            {
                await UpsertNumericIfHasValueAsync("design", "default_design_cost", dto.design.default_design_cost, now, ct);
            }

            // 7) PLATE PRICES
            if (dto.plate_prices != null)
            {
                await UpsertPlatePriceAsync("SMALL_37X45", dto.plate_prices.SMALL_37X45, now, ct);
                await UpsertPlatePriceAsync("SMALL_40X51", dto.plate_prices.SMALL_40X51, now, ct);
                await UpsertPlatePriceAsync("SMALL_45X55", dto.plate_prices.SMALL_45X55, now, ct);
                await UpsertPlatePriceAsync("MEDIUM_55X65", dto.plate_prices.MEDIUM_55X65, now, ct);
                await UpsertPlatePriceAsync("MEDIUM_60_5X74_5", dto.plate_prices.MEDIUM_60_5X74_5, now, ct);
                await UpsertPlatePriceAsync("MEDIUM_79X60", dto.plate_prices.MEDIUM_79X60, now, ct);
                await UpsertPlatePriceAsync("LARGE_79X103", dto.plate_prices.LARGE_79X103, now, ct);
                await UpsertPlatePriceAsync("LARGE_80X103", dto.plate_prices.LARGE_80X103, now, ct);
                await UpsertPlatePriceAsync("XLARGE_114X145", dto.plate_prices.XLARGE_114X145, now, ct);
                await UpsertPlatePriceAsync("XLARGE_132X163", dto.plate_prices.XLARGE_132X163, now, ct);
            }

            // 8) PAYMENT TERMS
            if (dto.payment_terms?.deposit_percent.HasValue == true)
            {
                var deposit = dto.payment_terms.deposit_percent.Value;
                if (deposit < 0) deposit = 0;
                if (deposit > 100) deposit = 100;

                var remaining = 100m - deposit;

                await UpsertNumericAsync("paymentTerms", "deposit_percent", deposit, now, ct);
                await UpsertNumericAsync("paymentTerms", "remaining_percent", remaining, now, ct);
            }

            if (dto.delivery_payment?.require_remaining_before_delivery.HasValue == true)
            {
                await UpsertBoolAsync(
                    "deliveryPayment",
                    "require_remaining_before_delivery",
                    dto.delivery_payment.require_remaining_before_delivery.Value,
                    now,
                    ct);
            }

            // 9) PLANNING
            if (dto.planning != null)
            {
                await UpsertNumericIfHasValueAsync("planning", "min_start_wait_hours", dto.planning.min_start_wait_hours, now, ct);

                await UpsertTimeTextIfHasValueAsync("planning", "work_start_time", dto.planning.work_start_time, now, ct);
                await UpsertTimeTextIfHasValueAsync("planning", "break_start_time", dto.planning.break_start_time, now, ct);
                await UpsertTimeTextIfHasValueAsync("planning", "break_end_time", dto.planning.break_end_time, now, ct);
                await UpsertTimeTextIfHasValueAsync("planning", "work_end_time", dto.planning.work_end_time, now, ct);
            }

            await _db.SaveChangesAsync(ct);
        }

        private async Task UpsertBoolAsync(
    string group,
    string key,
    bool value,
    DateTime now,
    CancellationToken ct)
        {
            var row = await _db.Set<estimate_config>()
                .FirstOrDefaultAsync(x => x.config_group == group && x.config_key == key, ct);

            if (row == null)
            {
                row = new estimate_config
                {
                    config_group = group,
                    config_key = key
                };

                await _db.Set<estimate_config>().AddAsync(row, ct);
            }

            row.value_bool = value;
            row.updated_at = now;
        }

        private async Task UpsertNumericIfHasValueAsync(
    string group,
    string key,
    decimal? value,
    DateTime now,
    CancellationToken ct)
        {
            if (!value.HasValue)
                return;

            await UpsertNumericAsync(group, key, value.Value, now, ct);
        }

        private async Task UpsertIntegerIfHasValueAsync(
            string group,
            string key,
            int? value,
            DateTime now,
            CancellationToken ct)
        {
            if (!value.HasValue)
                return;

            await UpsertNumericAsync(group, key, value.Value, now, ct);
        }

        private async Task UpsertNumericAsync(
            string group,
            string key,
            decimal value,
            DateTime now,
            CancellationToken ct)
        {
            var row = await _db.Set<estimate_config>()
                .FirstOrDefaultAsync(x => x.config_group == group && x.config_key == key, ct);

            if (row == null)
            {
                row = new estimate_config
                {
                    config_group = group,
                    config_key = key
                };
                await _db.Set<estimate_config>().AddAsync(row, ct);
            }

            row.value_num = value;
            row.updated_at = now;
        }

        private async Task UpsertTextAsync(
            string group,
            string key,
            string value,
            DateTime now,
            CancellationToken ct)
        {
            var row = await _db.Set<estimate_config>()
                .FirstOrDefaultAsync(x => x.config_group == group && x.config_key == key, ct);

            if (row == null)
            {
                row = new estimate_config
                {
                    config_group = group,
                    config_key = key
                };
                await _db.Set<estimate_config>().AddAsync(row, ct);
            }

            row.value_text = value;
            row.updated_at = now;
        }

        private async Task UpsertTimeTextIfHasValueAsync(
            string group,
            string key,
            string? value,
            DateTime now,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var normalized = NormalizeRequiredTime(value, key);
            await UpsertTextAsync(group, key, normalized, now, ct);
        }

        private static string NormalizeRequiredTime(string raw, string fieldName)
        {
            if (!TimeSpan.TryParse(raw, out var ts))
                throw new ArgumentException($"{fieldName} must be in HH:mm format");

            return $"{ts.Hours:D2}:{ts.Minutes:D2}";
        }

        private async Task UpsertProcessCostAsync(
            string processCode,
            ProcessCostItemUpdateDto? dto,
            DateTime now,
            CancellationToken ct)
        {
            if (dto == null)
                return;

            var row = await _db.Set<estimate_config>()
                .FirstOrDefaultAsync(x => x.config_group == "processCosts.by_process" && x.config_key == processCode, ct);

            if (row == null)
            {
                row = new estimate_config
                {
                    config_group = "processCosts.by_process",
                    config_key = processCode
                };
                await _db.Set<estimate_config>().AddAsync(row, ct);
            }

            if (dto.unit_price.HasValue)
                row.value_num = dto.unit_price.Value;

            if (!string.IsNullOrWhiteSpace(dto.unit))
                row.value_text = dto.unit.Trim();

            row.value_json = GetProcessCostMetaJson(processCode, row.value_json);
            row.updated_at = now;
        }

        private static string GetProcessCostMetaJson(string processCode, string? existingJson)
        {
            return processCode.ToUpperInvariant() switch
            {
                "IN" => "{\"note\":\"Công in offset 4 màu/m²\",\"process_name\":\"Công in\"}",
                "PHU" => "{\"note\":\"Công phủ/m²\",\"process_name\":\"Công phủ\"}",
                "CAN" => "{\"note\":\"Công cán màng BOPP/m²\",\"process_name\":\"Công cán\"}",
                "BOI" => "{\"note\":\"Công bồi carton/tờ\",\"process_name\":\"Công bồi\"}",
                "BE" => "{\"note\":\"Công bế khuôn/tờ\",\"process_name\":\"Công bế\"}",
                "RALO" => "{\"note\":\"Công xả khuôn/tờ\",\"process_name\":\"Công rã lô\"}",
                "DAN" => "{\"note\":\"Công dán hộp/sp\",\"process_name\":\"Công dán\"}",
                "DUT" => "{\"note\":\"Tính chung với bế\",\"process_name\":\"Công dứt\"}",
                "CAT" => "{\"note\":\"Không tính\",\"process_name\":\"Công cắt\"}",
                _ => existingJson ?? "{}"
            };
        }

        private async Task UpsertPlatePriceAsync(
            string plateKey,
            PlatePriceItemUpdateDto? dto,
            DateTime now,
            CancellationToken ct)
        {
            if (dto == null)
                return;

            var row = await _db.Set<estimate_config>()
                .FirstOrDefaultAsync(x => x.config_group == "platePrices" && x.config_key == plateKey, ct);

            if (row == null)
            {
                row = new estimate_config
                {
                    config_group = "platePrices",
                    config_key = plateKey
                };
                await _db.Set<estimate_config>().AddAsync(row, ct);
            }

            if (dto.price_per_plate.HasValue)
                row.value_num = dto.price_per_plate.Value;

            if (!string.IsNullOrWhiteSpace(dto.size_text))
                row.value_text = dto.size_text.Trim();

            row.updated_at = now;
        }
    }
}