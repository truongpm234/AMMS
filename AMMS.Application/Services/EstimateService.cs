using AMMS.Application.Interfaces;
using AMMS.Application.Rules;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Delivery;
using AMMS.Shared.DTOs.Discount;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Materials;

namespace AMMS.Application.Services
{
    /// <summary>
    /// Service ước lượng chi phí sản xuất với logic chi tiết
    /// </summary>
    public class EstimateService : IEstimateService
    {
        private readonly IMaterialRepository _materialRepo;
        private readonly ICostEstimateRepository _estimateRepo;
        private readonly IMachineRepository _machineRepo;

        public EstimateService(IMaterialRepository materialRepo, ICostEstimateRepository costEstimateRepository, IMachineRepository machineRepo)
        {
            _materialRepo = materialRepo;
            _estimateRepo = costEstimateRepository;
            _machineRepo = machineRepo;
        }

        private static TEnum ParseEnum<TEnum>(string value, string fieldName)
            where TEnum : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{fieldName} is required");

            if (!Enum.TryParse<TEnum>(value, true, out var result))
                throw new ArgumentException($"Invalid {fieldName}: {value}");

            return result;
        }

        /// <summary>
        /// Resolve ProductTypeCode từ product_type và form_product
        /// </summary>
        private static ProductTypeCode ResolveProductType(string productType, string? formProduct)
        {
            // Nếu product_type là dạng chung (HOP_MAU hoặc VO_HOP_GACH)
            if (productType.Equals("HOP_MAU", StringComparison.OrdinalIgnoreCase) ||
                productType.Equals("VO_HOP_GACH", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(formProduct))
                {
                    throw new ArgumentException(
                        $"form_product is required when product_type is '{productType}'. " +
                        $"Please specify the detailed product form (e.g., HOP_MAU_1LUOT_DON_GIAN, GACH_NOI_DIA_4SP)");
                }

                // Parse form_product
                return ParseEnum<ProductTypeCode>(formProduct, "form_product");
            }

            // Nếu product_type đã là giá trị cụ thể, parse trực tiếp
            return ParseEnum<ProductTypeCode>(productType, "product_type");
        }

        public async Task<PaperEstimateResponse> EstimatePaperAsync(PaperEstimateRequest req)
        {
            // =====================
            // 1. VALIDATION
            // =====================
            ValidateRequest(req);

            // =====================
            // 2. LẤY THÔNG TIN GIẤY
            // =====================
            var paper = await GetPaperMaterial(req.paper_code);
            int sheetW = paper.sheet_width_mm!.Value;
            int sheetH = paper.sheet_height_mm!.Value;

            // =====================
            // 3. RESOLVE PRODUCT TYPE
            // =====================
            var productType = ResolveProductType(req.product_type, req.form_product);

            // =====================
            // 4. TÍNH KÍCH THƯỚC TRIỂN KHAI
            // =====================
            var (printW, printH) = CalculatePrintSize(req, productType);

            // =====================
            // 5. TÍNH N-UP
            // =====================
            int nUp = CalculateNUp(sheetW, sheetH, printW, printH);

            // =====================
            // 6. TÍNH SỐ TỜ CƠ BẢN
            // =====================
            int sheetsBase = (int)Math.Ceiling(req.quantity / (decimal)nUp);

            // =====================
            // 7. TÍNH HAO HỤT THEO CÔNG ĐOẠN
            // =====================
            var wasteResult = CalculateWasteWithSmartScaling(req, sheetsBase, productType);

            // =====================
            // 8. CẢNH BÁO ĐƠN NHỎ
            // =====================
            string? warningMessage = GenerateWarningMessage(sheetsBase, wasteResult.TotalWaste);

            return new PaperEstimateResponse
            {
                paper_code = paper.code,
                sheet_width_mm = sheetW,
                sheet_height_mm = sheetH,
                print_width_mm = printW,
                print_height_mm = printH,
                n_up = nUp,
                quantity = req.quantity,
                sheets_base = sheetsBase,
                waste_printing = wasteResult.WastePrinting,
                waste_die_cutting = wasteResult.WasteDieCutting,
                waste_mounting = wasteResult.WasteMounting,
                waste_coating = wasteResult.WasteCoating,
                waste_lamination = wasteResult.WasteLamination,
                waste_gluing = wasteResult.WasteGluing,
                total_waste = wasteResult.TotalWaste,
                sheets_with_waste = sheetsBase + wasteResult.TotalWaste,
                waste_percent = Math.Round(wasteResult.WastePercent, 2),
                warning_message = warningMessage
            };
        }

        public async Task<CostEstimateResponse> CalculateCostEstimateAsync(CostEstimateRequest req)
        {
            // =====================
            // 1. VALIDATION
            // =====================
            ValidateCostRequest(req);

            // =====================
            // 2. LẤY THÔNG TIN GIẤY
            // =====================
            var paper = await GetPaperMaterial(req.paper.paper_code);
            decimal paperUnitPrice = paper.cost_price!.Value;

            // =====================
            // 3. TÍNH DIỆN TÍCH
            // =====================
            decimal sheetAreaM2 = (req.paper.sheet_width_mm / 1000m) * (req.paper.sheet_height_mm / 1000m);
            decimal totalAreaM2 = sheetAreaM2 * req.paper.sheets_with_waste;

            // =====================
            // 4. TÍNH CHI PHÍ GIẤY
            // =====================
            decimal paperCost = req.paper.sheets_with_waste * paperUnitPrice;

            // =====================
            // 5. RESOLVE PRODUCT TYPE & TÍNH CHI PHÍ VẬT LIỆU KHÁC
            // =====================
            var productType = ResolveProductType(req.product_type, req.form_product);
            var coatingType = ParseEnum<CoatingType>(req.coating_type ?? "NONE", "coating_type");
            var processes = ParseProcesses(req.production_processes);

            var materialCosts = CalculateMaterialCosts(
                totalAreaM2,
                productType,
                coatingType,
                processes,
                req.has_lamination
            );

            // =====================
            // 6. TỔNG VẬT LIỆU + KHẤU HAO
            // =====================
            decimal materialCost = paperCost + materialCosts.InkCost + materialCosts.CoatingGlueCost
                                 + materialCosts.MountingGlueCost + materialCosts.LaminationCost;

            decimal overheadPercent = SystemParameters.OVERHEAD_PERCENT;
            decimal overheadCost = materialCost * (overheadPercent / 100m);
            decimal baseCost = materialCost + overheadCost;

            // =====================
            // 7. RUSH ORDER
            // =====================
            var machines = await _machineRepo.GetActiveMachinesAsync();
            int productionDays = ProductionTimeCalculator.CalculateProductionDays(
                req.paper.sheets_with_waste,
                req.paper.quantity,
                processes,
                machines
            );

            var now = DateTime.UtcNow;
            var estimatedFinish = now.AddDays(productionDays);
            var rushResult = CalculateRushCost(req.desired_delivery_date, estimatedFinish, baseCost);

            // =====================
            // 8. SUBTOTAL
            // =====================
            decimal subtotal = baseCost + rushResult.RushAmount;

            // =====================
            // 9. CHIẾT KHẤU
            // =====================
            var discountResult = CalculateDiscount(subtotal, req.discount_percent);

            decimal finalTotal = subtotal - discountResult.DiscountAmount;

            await SaveCostEstimate(req, paperCost, paperUnitPrice, materialCosts, materialCost,
                overheadPercent, overheadCost, baseCost, rushResult, subtotal, discountResult,
                finalTotal, estimatedFinish, now, totalAreaM2, coatingType);

            return BuildCostResponse(
                paperCost, req.paper.sheets_with_waste, paperUnitPrice,
                materialCosts, materialCost, overheadPercent, overheadCost,
                baseCost, rushResult, subtotal, discountResult, finalTotal,
                estimatedFinish, totalAreaM2, coatingType
            );
        }

        // ==================== PRIVATE HELPER METHODS ====================

        private void ValidateRequest(PaperEstimateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.paper_code))
                throw new ArgumentException("paper_code is required");
            if (req.quantity <= 0)
                throw new ArgumentException("quantity must be > 0");
            if (req.length_mm <= 0 || req.width_mm <= 0 || req.height_mm <= 0)
                throw new ArgumentException("length_mm, width_mm, height_mm must be > 0");
            if (req.bleed_mm < 0)
                throw new ArgumentException("bleed_mm must be >= 0");

            // Validate form_product nếu product_type là dạng chung
            if (req.product_type.Equals("HOP_MAU", StringComparison.OrdinalIgnoreCase) ||
                req.product_type.Equals("VO_HOP_GACH", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(req.form_product))
                {
                    throw new ArgumentException(
                        $"form_product is required when product_type is '{req.product_type}'");
                }
            }
        }

        private void ValidateCostRequest(CostEstimateRequest req)
        {
            if (req.paper == null)
                throw new ArgumentException("Paper information is required");
            if (req.paper.sheets_with_waste <= 0)
                throw new ArgumentException("Sheets with waste must be greater than 0");
            if (req.discount_percent < 0 || req.discount_percent > 100)
                throw new ArgumentException("Discount percent must be between 0 and 100");

            // Validate form_product nếu product_type là dạng chung
            if (req.product_type.Equals("HOP_MAU", StringComparison.OrdinalIgnoreCase) ||
                req.product_type.Equals("VO_HOP_GACH", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(req.form_product))
                {
                    throw new ArgumentException(
                        $"form_product is required when product_type is '{req.product_type}'");
                }
            }
        }

        private async Task<material> GetPaperMaterial(string paperCode)
        {
            var paper = await _materialRepo.GetByCodeAsync(paperCode)
                ?? throw new KeyNotFoundException($"Paper not found: {paperCode}");

            if (paper.sheet_width_mm is null || paper.sheet_height_mm is null)
                throw new InvalidOperationException("Missing sheet size in materials.");

            if (paper.cost_price is null)
                throw new InvalidOperationException($"Paper cost_price missing for {paper.code}");

            return paper;
        }

        private (int printW, int printH) CalculatePrintSize(PaperEstimateRequest req, ProductTypeCode productType)
        {
            int tabWidth = req.glue_tab_mm > 0 ? req.glue_tab_mm : 20;
            bool isBoxProduct = productType.ToString().StartsWith("HOP_MAU");
            bool isBrickProduct = productType.ToString().StartsWith("GACH_");

            int printW, printH;

            if (isBrickProduct)
            {
                // HỘP GẠCH (dạng tray)
                printW = req.length_mm + 2 * req.height_mm + 2 * req.bleed_mm;
                printH = req.width_mm + 2 * req.height_mm + 2 * req.bleed_mm;
            }
            else if (isBoxProduct)
            {
                // HỘP CARTON
                printW = 2 * (req.length_mm + req.width_mm) + tabWidth + 2 * req.bleed_mm;

                if (req.is_one_side_box)
                    printH = req.width_mm + req.height_mm + 2 * req.bleed_mm;
                else
                    printH = (2 * req.width_mm + req.height_mm) + 2 * req.bleed_mm;
            }
            else
            {
                // Mặc định
                printW = 2 * (req.length_mm + req.width_mm) + tabWidth + 2 * req.bleed_mm;
                printH = (2 * req.width_mm + req.height_mm) + 2 * req.bleed_mm;
            }

            return (printW, printH);
        }

        private int CalculateNUp(int sheetW, int sheetH, int printW, int printH)
        {
            int n1 = (sheetW / printW) * (sheetH / printH);
            int n2 = (sheetW / printH) * (sheetH / printW);
            int nUp = Math.Max(n1, n2);

            if (nUp <= 0)
                throw new InvalidOperationException(
                    $"Print size ({printW}×{printH}mm) does not fit paper sheet ({sheetW}×{sheetH}mm).");

            return nUp;
        }

        private class WasteResult
        {
            public int WastePrinting { get; set; }
            public int WasteDieCutting { get; set; }
            public int WasteMounting { get; set; }
            public int WasteCoating { get; set; }
            public int WasteLamination { get; set; }
            public int WasteGluing { get; set; }
            public int TotalWaste { get; set; }
            public decimal WastePercent { get; set; }
        }

        private WasteResult CalculateWasteWithSmartScaling(PaperEstimateRequest req, int sheetsBase, ProductTypeCode productType)
        {
            var coatingType = ParseEnum<CoatingType>(req.coating_type ?? "NONE", "coating_type");
            var processes = ParseProcesses(req.production_processes);

            // Tính hao hụt IN với smart scaling
            int wastePrinting = CalculateSmartPrintingWaste(productType, req.number_of_plates, sheetsBase);

            // Tính hao hụt các công đoạn khác
            int wasteDieCutting = processes.Contains(ProcessType.BE)
                ? CalculateSmartProcessWaste(sheetsBase, "BE")
                : 0;

            int wasteMounting = processes.Contains(ProcessType.BOI)
                ? CalculateSmartProcessWaste(sheetsBase, "BOI")
                : 0;

            int wasteCoating = processes.Contains(ProcessType.PHU)
                ? CalculateSmartCoatingWaste(sheetsBase, coatingType)
                : 0;

            int wasteLamination = processes.Contains(ProcessType.CAN_MANG)
                ? CalculateSmartLaminationWaste(sheetsBase)
                : 0;

            int wasteGluing = processes.Contains(ProcessType.DAN)
                ? WasteCalculationRules.GluingWaste.Calculate(req.quantity)
                : 0;

            int totalWaste = wastePrinting + wasteDieCutting + wasteMounting
                           + wasteCoating + wasteLamination + wasteGluing;

            decimal wastePercent = sheetsBase > 0
                ? (totalWaste / (decimal)sheetsBase) * 100m
                : 0m;

            return new WasteResult
            {
                WastePrinting = wastePrinting,
                WasteDieCutting = wasteDieCutting,
                WasteMounting = wasteMounting,
                WasteCoating = wasteCoating,
                WasteLamination = wasteLamination,
                WasteGluing = wasteGluing,
                TotalWaste = totalWaste,
                WastePercent = wastePercent
            };
        }

        private int CalculateSmartPrintingWaste(ProductTypeCode productType, int numberOfPlates, int sheetsBase)
        {
            int standardWaste = WasteCalculationRules.PrintingWaste.GetBaseWaste(productType);

            // Thêm cho cao bản
            if (productType.ToString().StartsWith("HOP_MAU") && numberOfPlates > 0)
            {
                standardWaste += numberOfPlates * WasteCalculationRules.PrintingWaste.PER_PLATE;
            }
            return standardWaste;
        }

        private int CalculateSmartProcessWaste(int sheetsBase, string processType)
        {
            int standardWaste = processType == "BE"
                ? WasteCalculationRules.DieCuttingWaste.Calculate(sheetsBase)
                : WasteCalculationRules.MountingWaste.Calculate(sheetsBase);
            return standardWaste;
        }

        private int CalculateSmartCoatingWaste(int sheetsBase, CoatingType coatingType)
        {
            int standardWaste = WasteCalculationRules.CoatingWaste.Calculate(sheetsBase, coatingType);

            if (standardWaste == 0) return 0;
            return standardWaste;
        }

        private int CalculateSmartLaminationWaste(int sheetsBase)
        {
            int standardWaste = WasteCalculationRules.LaminationWaste.Calculate(sheetsBase);
            return standardWaste;
        }

        private string? GenerateWarningMessage(int sheetsBase, int totalWaste)
        {
            int sheetsWithWaste = sheetsBase + totalWaste;
            decimal extraCostPercent = ((sheetsWithWaste - sheetsBase) / (decimal)sheetsBase) * 100m;

            return $"⚠️ Đơn hàng nhỏ: Đơn hàng của bạn cần {sheetsBase} tờ, " +
                   $"nhưng hao hụt là {totalWaste} tờ ({extraCostPercent:F0}%), " +
                   $"tổng cộng {sheetsWithWaste} tờ.";
        }

        private MaterialCostResult CalculateMaterialCosts(decimal totalAreaM2, ProductTypeCode productType, CoatingType coatingType, List<ProcessType> processes, bool hasLamination)
        {
            var result = new MaterialCostResult();

            // MỰC IN
            result.InkRate = MaterialRates.InkRates.GetRate(productType);
            result.InkWeightKg = totalAreaM2 * result.InkRate;
            result.InkCost = result.InkWeightKg * MaterialPrices.INK_PRICE_PER_KG;

            // KEO PHỦ
            if (processes.Contains(ProcessType.PHU))
            {
                result.CoatingGlueRate = MaterialRates.CoatingGlueRates.GetRate(coatingType);
                result.CoatingGlueWeightKg = totalAreaM2 * result.CoatingGlueRate;
                result.CoatingGlueCost = result.CoatingGlueWeightKg *
                    MaterialPrices.GetCoatingGluePrice(coatingType);
            }

            // KEO BỒI
            if (processes.Contains(ProcessType.BOI))
            {
                result.MountingGlueRate = MaterialRates.MountingGlueRates.RATE;
                result.MountingGlueWeightKg = totalAreaM2 * result.MountingGlueRate;
                result.MountingGlueCost = result.MountingGlueWeightKg *
                    MaterialPrices.MOUNTING_GLUE_PER_KG;
            }

            // MÀNG CÁN
            if (processes.Contains(ProcessType.CAN_MANG) || hasLamination)
            {
                result.LaminationRate = MaterialRates.LaminationRates.RATE_12MIC;
                result.LaminationWeightKg = totalAreaM2 * result.LaminationRate;
                result.LaminationCost = result.LaminationWeightKg *
                    MaterialPrices.LAMINATION_PER_KG;
            }

            return result;
        }

        private RushResult CalculateRushCost(DateTime desiredDate, DateTime estimatedFinish, decimal baseCost)
        {
            var result = new RushResult();

            if (desiredDate >= estimatedFinish)
                return result;

            result.DaysEarly = (int)(estimatedFinish - desiredDate).TotalDays;

            if (result.DaysEarly >= SystemParameters.RUSH_THRESHOLD_DAYS)
            {
                result.IsRush = true;
                result.RushPercent = SystemParameters.GetRushPercent(result.DaysEarly);
                result.RushAmount = baseCost * (result.RushPercent / 100m);
            }

            return result;
        }

        private DiscountResult CalculateDiscount(decimal subtotal, decimal discountPercent)
        {
            return new DiscountResult
            {
                DiscountPercent = Math.Max(0, Math.Min(100, discountPercent)),
                DiscountAmount = subtotal * (discountPercent / 100m)
            };
        }

        // ==================== PROCESS HELPERS ====================

        private List<ProcessType> ParseProcesses(string productionProcesses)
        {
            return productionProcesses
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => ParseEnum<ProcessType>(p.Trim(), "process"))
                .ToList();
        }

        private async Task SaveCostEstimate(
            CostEstimateRequest req,
            decimal paperCost,
            decimal paperUnitPrice,
            MaterialCostResult materialCosts,
            decimal materialCost,
            decimal overheadPercent,
            decimal overheadCost,
            decimal baseCost,
            RushResult rushResult,
            decimal subtotal,
            DiscountResult discountResult,
            decimal finalTotal,
            DateTime estimatedFinish,
            DateTime now,
            decimal totalAreaM2,
            CoatingType coatingType)
        {
            var entity = new cost_estimate
            {
                order_request_id = req.order_request_id,

                // Chi phí giấy
                paper_cost = Math.Round(paperCost, 2),
                paper_sheets_used = req.paper.sheets_with_waste,
                paper_unit_price = Math.Round(paperUnitPrice, 2),

                // Chi phí mực
                ink_cost = Math.Round(materialCosts.InkCost, 2),
                ink_weight_kg = Math.Round(materialCosts.InkWeightKg, 4),
                ink_rate_per_m2 = Math.Round(materialCosts.InkRate, 6),

                // Chi phí keo phủ
                coating_glue_cost = Math.Round(materialCosts.CoatingGlueCost, 2),
                coating_glue_weight_kg = Math.Round(materialCosts.CoatingGlueWeightKg, 4),
                coating_glue_rate_per_m2 = Math.Round(materialCosts.CoatingGlueRate, 6),
                coating_type = coatingType.ToString(),

                // Chi phí keo bồi
                mounting_glue_cost = Math.Round(materialCosts.MountingGlueCost, 2),
                mounting_glue_weight_kg = Math.Round(materialCosts.MountingGlueWeightKg, 4),
                mounting_glue_rate_per_m2 = Math.Round(materialCosts.MountingGlueRate, 6),

                // Chi phí màng
                lamination_cost = Math.Round(materialCosts.LaminationCost, 2),
                lamination_weight_kg = Math.Round(materialCosts.LaminationWeightKg, 4),
                lamination_rate_per_m2 = Math.Round(materialCosts.LaminationRate, 6),

                // Tổng vật liệu và khấu hao
                material_cost = Math.Round(materialCost, 2),
                overhead_percent = overheadPercent,
                overhead_cost = Math.Round(overheadCost, 2),

                // Chi phí cơ bản
                base_cost = Math.Round(baseCost, 2),

                // Rush order
                is_rush = rushResult.IsRush,
                rush_percent = rushResult.RushPercent,
                rush_amount = Math.Round(rushResult.RushAmount, 2),
                days_early = rushResult.DaysEarly,

                // Subtotal
                subtotal = Math.Round(subtotal, 2),

                // Chiết khấu
                discount_percent = discountResult.DiscountPercent,
                discount_amount = Math.Round(discountResult.DiscountAmount, 2),

                // Tổng cuối
                final_total_cost = Math.Round(finalTotal, 2),

                // Thông tin khác
                estimated_finish_date = DateTime.SpecifyKind(estimatedFinish, DateTimeKind.Unspecified),
                desired_delivery_date = DateTime.SpecifyKind(req.desired_delivery_date, DateTimeKind.Unspecified),
                created_at = DateTime.SpecifyKind(now, DateTimeKind.Unspecified),

                // Chi tiết giấy
                sheets_required = req.paper.sheets_base,
                sheets_waste = req.paper.total_waste,
                sheets_total = req.paper.sheets_with_waste,

                // Diện tích
                total_area_m2 = Math.Round(totalAreaM2, 4)
            };

            await _estimateRepo.AddAsync(entity);
            await _estimateRepo.SaveChangesAsync();
        }

        // ==================== BUILD RESPONSE ====================

        private CostEstimateResponse BuildCostResponse(
            decimal paperCost,
            int paperSheetsUsed,
            decimal paperUnitPrice,
            MaterialCostResult materialCosts,
            decimal materialCost,
            decimal overheadPercent,
            decimal overheadCost,
            decimal baseCost,
            RushResult rushResult,
            decimal subtotal,
            DiscountResult discountResult,
            decimal finalTotal,
            DateTime estimatedFinish,
            decimal totalAreaM2,
            CoatingType coatingType)
        {
            var response = new CostEstimateResponse
            {
                // Chi phí giấy
                paper_cost = Math.Round(paperCost, 2),
                paper_sheets_used = paperSheetsUsed,
                paper_unit_price = Math.Round(paperUnitPrice, 2),

                // Chi phí mực
                ink_cost = Math.Round(materialCosts.InkCost, 2),
                ink_weight_kg = Math.Round(materialCosts.InkWeightKg, 4),
                ink_rate_per_m2 = Math.Round(materialCosts.InkRate, 6),
                ink_unit_price = MaterialPrices.INK_PRICE_PER_KG,

                // Chi phí keo phủ
                coating_glue_cost = Math.Round(materialCosts.CoatingGlueCost, 2),
                coating_glue_weight_kg = Math.Round(materialCosts.CoatingGlueWeightKg, 4),
                coating_glue_rate_per_m2 = Math.Round(materialCosts.CoatingGlueRate, 6),
                coating_glue_unit_price = MaterialPrices.GetCoatingGluePrice(coatingType),
                coating_type = coatingType.ToString(),

                // Chi phí keo bồi
                mounting_glue_cost = Math.Round(materialCosts.MountingGlueCost, 2),
                mounting_glue_weight_kg = Math.Round(materialCosts.MountingGlueWeightKg, 4),
                mounting_glue_rate_per_m2 = Math.Round(materialCosts.MountingGlueRate, 6),
                mounting_glue_unit_price = MaterialPrices.MOUNTING_GLUE_PER_KG,

                // Chi phí màng
                lamination_cost = Math.Round(materialCosts.LaminationCost, 2),
                lamination_weight_kg = Math.Round(materialCosts.LaminationWeightKg, 4),
                lamination_rate_per_m2 = Math.Round(materialCosts.LaminationRate, 6),
                lamination_unit_price = MaterialPrices.LAMINATION_PER_KG,

                // Tổng vật liệu
                material_cost = Math.Round(materialCost, 2),

                // Khấu hao
                overhead_percent = overheadPercent,
                overhead_cost = Math.Round(overheadCost, 2),

                // Chi phí cơ bản
                base_cost = Math.Round(baseCost, 2),

                // Rush order
                is_rush = rushResult.IsRush,
                rush_percent = rushResult.RushPercent,
                rush_amount = Math.Round(rushResult.RushAmount, 2),
                days_early = rushResult.DaysEarly,

                // Subtotal
                subtotal = Math.Round(subtotal, 2),

                // Chiết khấu
                discount_percent = discountResult.DiscountPercent,
                discount_amount = Math.Round(discountResult.DiscountAmount, 2),

                // Tổng cuối
                final_total_cost = Math.Round(finalTotal, 2),

                // Thông tin khác
                estimated_finish_date = DateTime.SpecifyKind(estimatedFinish, DateTimeKind.Utc),
                total_area_m2 = Math.Round(totalAreaM2, 4),

                // Chi tiết công đoạn
                process_details = BuildProcessDetails(
                    paperCost, paperSheetsUsed, paperUnitPrice,
                    materialCosts, coatingType
                )
            };

            return response;
        }

        private List<ProcessCostDetail> BuildProcessDetails(
            decimal paperCost,
            int paperSheetsUsed,
            decimal paperUnitPrice,
            MaterialCostResult materialCosts,
            CoatingType coatingType)
        {
            var details = new List<ProcessCostDetail>();

            // Giấy
            details.Add(new ProcessCostDetail
            {
                process_name = "Giấy",
                waste_sheets = paperSheetsUsed,
                material_used_kg = 0,
                unit_price = paperUnitPrice,
                total_cost = paperCost,
                note = $"Sử dụng {paperSheetsUsed} tờ"
            });

            // Mực in
            if (materialCosts.InkCost > 0)
            {
                details.Add(new ProcessCostDetail
                {
                    process_name = "Mực in",
                    waste_sheets = 0,
                    material_used_kg = materialCosts.InkWeightKg,
                    unit_price = MaterialPrices.INK_PRICE_PER_KG,
                    total_cost = materialCosts.InkCost,
                    note = $"Định mức: {materialCosts.InkRate:F6} kg/m²"
                });
            }

            // Keo phủ
            if (materialCosts.CoatingGlueCost > 0)
            {
                details.Add(new ProcessCostDetail
                {
                    process_name = $"Keo phủ ({coatingType})",
                    waste_sheets = 0,
                    material_used_kg = materialCosts.CoatingGlueWeightKg,
                    unit_price = MaterialPrices.GetCoatingGluePrice(coatingType),
                    total_cost = materialCosts.CoatingGlueCost,
                    note = $"Định mức: {materialCosts.CoatingGlueRate:F6} kg/m²"
                });
            }

            // Keo bồi
            if (materialCosts.MountingGlueCost > 0)
            {
                details.Add(new ProcessCostDetail
                {
                    process_name = "Keo bồi",
                    waste_sheets = 0,
                    material_used_kg = materialCosts.MountingGlueWeightKg,
                    unit_price = MaterialPrices.MOUNTING_GLUE_PER_KG,
                    total_cost = materialCosts.MountingGlueCost,
                    note = $"Định mức: {materialCosts.MountingGlueRate:F6} kg/m²"
                });
            }

            // Màng cán
            if (materialCosts.LaminationCost > 0)
            {
                details.Add(new ProcessCostDetail
                {
                    process_name = "Màng cán",
                    waste_sheets = 0,
                    material_used_kg = materialCosts.LaminationWeightKg,
                    unit_price = MaterialPrices.LAMINATION_PER_KG,
                    total_cost = materialCosts.LaminationCost,
                    note = $"Định mức: {materialCosts.LaminationRate:F6} kg/m² (12g màng + 5g keo)"
                });
            }

            return details;
        }

        public async Task AdjustManualCostAsync(int estimateId, decimal? discountPercent, string? note)
        {
            var estimate = await _estimateRepo.GetByIdAsync(estimateId)
                ?? throw new Exception("Estimate not found");

            var percent = discountPercent ?? 0m;

            if (percent < 0m || percent > 100m)
                throw new ArgumentException("discount_percent must be between 0 and 100");

            decimal subtotal = estimate.subtotal;

            if (subtotal <= 0m)
            {
                if (estimate.system_total_cost > 0m) subtotal = estimate.system_total_cost;
                else subtotal = estimate.base_cost + estimate.rush_amount;
            }

            var discountAmount = Math.Round(subtotal * percent / 100m, 2);
            var final = subtotal - discountAmount;
            if (final < 0m) final = 0m;

            estimate.discount_percent = percent;
            estimate.discount_amount = discountAmount;
            estimate.subtotal = subtotal;
            estimate.final_total_cost = Math.Round(final, 2);
            estimate.cost_note = note;

            await _estimateRepo.SaveChangesAsync();
        }

        public async Task<cost_estimate?> GetEstimateByIdAsync(int estimateId)
        {
            return await _estimateRepo.GetByIdAsync(estimateId);
        }

        public async Task<cost_estimate?> GetEstimateByOrderRequestIdAsync(int orderRequestId)
        {
            return await _estimateRepo.GetByOrderRequestIdAsync(orderRequestId);
        }
    }
}