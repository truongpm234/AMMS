using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.Estimates;

namespace AMMS.Application.Services
{
    public class EstimateService : IEstimateService
    {
        private readonly IMaterialRepository _materialRepo;
        private readonly ICostEstimateRepository _estimateRepo;

        // Định mức vật liệu
        private const decimal INK_RATE_GACH_NOI_DIA = 0.0003m; // kg/m2
        private const decimal INK_RATE_HOP_MAU = 0.0009m; // kg/m2
        private const decimal INK_RATE_GACH_NHIEU_MAU = 0.001m; // kg/m2
        private const decimal COATING_GLUE_RATE = 0.004m; // kg/m2
        private const decimal MOUNTING_GLUE_RATE = 0.004m; // kg/m2
        private const decimal LAMINATION_RATE = 0.017m; // kg/m2 (12g màng + 5g keo)

        // Giá vật liệu (VND)
        private const decimal INK_PRICE_PER_KG = 150000m; // Giả định giá mực
        private const decimal COATING_GLUE_PRICE_PER_KG = 80000m; // Giả định giá keo phủ
        private const decimal MOUNTING_GLUE_PRICE_PER_KG = 60000m; // Giả định giá keo bồi
        private const decimal LAMINATION_PRICE_PER_KG = 200000m; // Giả định giá màng

        public EstimateService(IMaterialRepository materialRepo, ICostEstimateRepository costEstimateRepository)
        {
            _materialRepo = materialRepo;
            _estimateRepo = costEstimateRepository;
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
        public async Task<PaperEstimateResponse> EstimatePaperAsync(PaperEstimateRequest req)
        {
            // Validation
            if (string.IsNullOrWhiteSpace(req.paper_code))
                throw new ArgumentException("paper_code is required");
            if (req.quantity <= 0)
                throw new ArgumentException("quantity must be > 0");
            if (req.length_mm < 0 || req.width_mm < 0 || req.height_mm < 0)
                throw new ArgumentException("length_mm/width_mm/height_mm must be >= 0");
            if (req.allowance_mm < 0 || req.bleed_mm < 0)
                throw new ArgumentException("allowance_mm/bleed_mm must be >= 0");

            var paper = await _materialRepo.GetByCodeAsync(req.paper_code)
                ?? throw new KeyNotFoundException($"Paper not found: {req.paper_code}");

            if (paper.sheet_width_mm is null || paper.sheet_height_mm is null || paper.sheet_length_mm is null)
                throw new InvalidOperationException("Missing sheet size in materials.");

            int sheetW = paper.sheet_width_mm.Value;
            int sheetH = paper.sheet_height_mm.Value;
            int sheetL = paper.sheet_length_mm.Value;

            // KÍCH THƯỚC KHI IN
            // Chiều rộng in (W) = 2 * chu vi đáy + chừa gấp + chừa xén
            int printW = 2 * (req.length_mm + req.width_mm) + req.allowance_mm + 2 * req.bleed_mm;

            // Chiều cao in (H) = chiều cao hộp + chiều rộng hộp + chừa gấp + chừa xén
            int printH = (req.height_mm + req.width_mm) + req.allowance_mm + 2 * req.bleed_mm;

            int printL = req.length_mm + req.allowance_mm + 2 * req.bleed_mm;

            // Kiểm tra khả năng in
            bool fitWH = printW <= sheetW && printH <= sheetH;
            bool fitHW = printH <= sheetW && printW <= sheetH;
            bool fitWL = printW <= sheetW && printL <= sheetL;
            bool fitLW = printL <= sheetW && printW <= sheetL;
            bool fitHL = printH <= sheetW && printL <= sheetL;
            bool fitLH = printL <= sheetW && printH <= sheetL;

            if (!fitWH && !fitHW && !fitWL && !fitLW && !fitHL && !fitLH)
                throw new InvalidOperationException("Print size does not fit paper in any orientation.");

            // Tính N-up
            int n_wh = fitWH ? (sheetW / printW) * (sheetH / printH) : 0;
            int n_hw = fitHW ? (sheetW / printH) * (sheetH / printW) : 0;
            int n_wl = fitWL ? (sheetW / printW) * (sheetL / printL) : 0;
            int n_lw = fitLW ? (sheetW / printL) * (sheetL / printW) : 0;
            int n_hl = fitHL ? (sheetW / printH) * (sheetL / printL) : 0;
            int n_lh = fitLH ? (sheetW / printL) * (sheetL / printH) : 0;

            int nUp = Math.Max(
                Math.Max(n_wh, n_hw),
                Math.Max(Math.Max(n_wl, n_lw), Math.Max(n_hl, n_lh))
            );

            if (nUp <= 0) nUp = 1;

            // Số tờ cơ bản (không tính hao hụt)
            int sheetsBase = (int)Math.Ceiling(req.quantity / (decimal)nUp);

            // TÍNH HAO HỤT THEO TỪNG CÔNG ĐOẠN
            var productType = ParseEnum<ProductTypeCode>(req.product_type, "product_type");
            var coatingType = ParseEnum<CoatingType>(req.coating_type ?? "NONE", "coating_type");

            var processes = req.production_processes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => ParseEnum<ProcessType>(p.Trim(), "process"))
                .ToList();


            int wastePrinting = CalculatePrintingWaste(productType, req.number_of_plates);
            int wasteDieCutting = processes.Contains(ProcessType.BE) ? CalculateProcessWaste(sheetsBase, ProcessType.BE) : 0;
            int wasteMounting = processes.Contains(ProcessType.BOI) ? CalculateProcessWaste(sheetsBase, ProcessType.BOI) : 0;
            int wasteCoating = processes.Contains(ProcessType.PHU) ? CalculateCoatingWaste(sheetsBase, coatingType) : 0;
            int wasteLamination = processes.Contains(ProcessType.CAN_MANG) ? CalculateLaminationWaste(sheetsBase) : 0;
            int wasteGluing = processes.Contains(ProcessType.DAN) ? CalculateGluingWaste(req.quantity) : 0;

            int totalWaste = wastePrinting + wasteDieCutting + wasteMounting + wasteCoating + wasteLamination + wasteGluing;

            int sheetsWithWaste = sheetsBase + totalWaste;
            decimal wastePercent = sheetsBase > 0 ? (totalWaste / (decimal)sheetsBase) * 100m : 0m;

            return new PaperEstimateResponse
            {
                paper_code = paper.code,
                sheet_width_mm = sheetW,
                sheet_height_mm = sheetH,
                sheet_length_mm = sheetL,
                print_width_mm = printW,
                print_height_mm = printH,
                print_length_mm = printL,
                n_up = nUp,
                quantity = req.quantity,
                sheets_base = sheetsBase,
                waste_printing = wastePrinting,
                waste_die_cutting = wasteDieCutting,
                waste_mounting = wasteMounting,
                waste_coating = wasteCoating,
                waste_lamination = wasteLamination,
                waste_gluing = wasteGluing,
                total_waste = totalWaste,
                sheets_with_waste = sheetsWithWaste,
                waste_percent = Math.Round(wastePercent, 2)
            };
        }

        private int CalculatePrintingWaste(ProductTypeCode productType, int numberOfPlates)
        {
            int baseWaste = productType switch
            {
                ProductTypeCode.GACH_1MAU => 50,
                ProductTypeCode.GACH_XUAT_KHAU_DON_GIAN => 120,
                ProductTypeCode.GACH_XUAT_KHAU_TERACON => 200,
                ProductTypeCode.GACH_NOI_DIA_4SP => 150,
                ProductTypeCode.GACH_NOI_DIA_6SP => 180,

                ProductTypeCode.HOP_MAU_1LUOT_DON_GIAN => 200,
                ProductTypeCode.HOP_MAU_1LUOT_THUONG => 230,
                ProductTypeCode.HOP_MAU_1LUOT_KHO => 250,
                ProductTypeCode.HOP_MAU_AQUA_DOI => 450,
                ProductTypeCode.HOP_MAU_2LUOT => 330,

                _ => 200
            };

            if (productType.ToString().StartsWith("HOP_MAU") && numberOfPlates > 0)
            {
                baseWaste += numberOfPlates * 10;
            }


            // Thêm 10 tờ cho mỗi cao bản (chỉ áp dụng cho hộp màu)
            if (productType.ToString().StartsWith("HOP_MAU") && numberOfPlates > 0)
            {
                baseWaste += numberOfPlates * 10;
            }

            return baseWaste;
        }

        private int CalculateProcessWaste(int sheets, ProcessType processType)
        {
            // Áp dụng cho cả BẾ và BỒI
            if (sheets < 5000)
                return 20;
            else if (sheets < 20000)
                return 30;
            else if (sheets <= 40000)
                return 40;
            else
                return 40;
        }

        private int CalculateCoatingWaste(int sheets, CoatingType coatingType)
        {
            if (coatingType == CoatingType.KEO_NUOC)
                return 0;

            if (coatingType == CoatingType.KEO_DAU)
                return sheets < 10000 ? 20 : 30;

            return 0;
        }


        private int CalculateLaminationWaste(int sheets)
        {
            return sheets < 10000 ? 20 : 30;
        }

        private int CalculateGluingWaste(int quantity)
        {
            // Dán: 20-30 hộp (tính theo sản phẩm, không phải tờ)
            return 25; // Trung bình
        }

        public async Task<CostEstimateResponse> CalculateCostEstimateAsync(CostEstimateRequest req)
        {
            if (req.paper == null)
                throw new ArgumentException("Paper information is required");

            if (req.paper.sheets_with_waste <= 0)
                throw new ArgumentException("Sheets with waste must be greater than 0");

            // Lấy thông tin giấy
            var paper = await _materialRepo.GetByCodeAsync(req.paper.paper_code)
                ?? throw new KeyNotFoundException($"Paper not found: {req.paper.paper_code}");

            if (paper.cost_price == null)
                throw new InvalidOperationException($"Paper cost_price missing for {paper.code}");

            // Tính diện tích (m2)
            decimal sheetAreaM2 = (req.paper.sheet_width_mm / 1000m) * (req.paper.sheet_height_mm / 1000m);
            decimal totalAreaM2 = sheetAreaM2 * req.paper.sheets_with_waste;

            // Tính chi phí giấy
            decimal paperCostPerSheet = paper.cost_price.Value;
            decimal totalPaperCost = req.paper.sheets_with_waste * paperCostPerSheet;

            // Tính chi phí mực in
            var productType = ParseEnum<ProductTypeCode>(req.product_type, "product_type");
            decimal inkRate = GetInkRate(productType);
            decimal inkWeightKg = totalAreaM2 * inkRate;
            decimal inkCost = inkWeightKg * INK_PRICE_PER_KG;

            // Tính chi phí keo phủ
            decimal coatingGlueCost = 0m;
            var coatingType = ParseEnum<CoatingType>(req.coating_type ?? "NONE", "coating_type");

            var processes = req.production_processes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => ParseEnum<ProcessType>(p.Trim(), "process"))
                .ToList();


            if (processes.Contains(ProcessType.PHU) && coatingType != CoatingType.NONE)
            {
                (decimal rate, decimal price) = coatingType switch
                {
                    CoatingType.KEO_NUOC => (0.003m, 70000m),
                    CoatingType.KEO_DAU => (0.004m, 80000m),
                    _ => (0m, 0m)
                };

                coatingGlueCost = totalAreaM2 * rate * price;
            }

            // Tính chi phí keo bồi
            decimal mountingGlueCost = 0m;
            if (processes.Contains(ProcessType.BOI))
            {
                decimal mountingGlueWeightKg = totalAreaM2 * MOUNTING_GLUE_RATE;
                mountingGlueCost = mountingGlueWeightKg * MOUNTING_GLUE_PRICE_PER_KG;
            }

            // Tính chi phí màng
            decimal laminationCost = 0m;
            if (processes.Contains(ProcessType.CAN_MANG) || req.has_lamination)
            {
                decimal laminationWeightKg = totalAreaM2 * LAMINATION_RATE;
                laminationCost = laminationWeightKg * LAMINATION_PRICE_PER_KG;
            }

            // Tổng chi phí vật liệu
            decimal materialCost = totalPaperCost + inkCost + coatingGlueCost +
                                 mountingGlueCost + laminationCost;

            // Khấu hao máy móc + NVL khác (10% trên vật liệu)
            decimal overheadCost = materialCost * 0.10m;
            decimal baseCost = materialCost + overheadCost;

            // Ngày hoàn thành dự kiến (5 ngày làm việc)
            var now = DateTime.UtcNow;
            var estimatedFinish = now.AddDays(5);

            bool isRush = false;
            decimal rushPercent = 0m;

            if (req.desired_delivery_date < estimatedFinish)
            {
                int daysEarly = (int)(estimatedFinish - req.desired_delivery_date).TotalDays;

                if (daysEarly == 1)
                {
                    isRush = true;
                    rushPercent = 5m;
                }
                else if (daysEarly >= 2 && daysEarly <= 3)
                {
                    isRush = true;
                    rushPercent = 20m;
                }
                else if (daysEarly >= 4)
                {
                    isRush = true;
                    rushPercent = 40m;
                }
            }

            decimal rushAmount = baseCost * rushPercent / 100;
            decimal systemTotalCost = baseCost + rushAmount;

            // Lưu vào database
            var entity = new cost_estimate
            {
                order_request_id = req.order_request_id,
                paper_cost = Math.Round(totalPaperCost, 2),
                ink_cost = Math.Round(inkCost, 2),
                coating_glue_cost = Math.Round(coatingGlueCost, 2),
                mounting_glue_cost = Math.Round(mountingGlueCost, 2),
                lamination_cost = Math.Round(laminationCost, 2),
                material_cost = Math.Round(materialCost, 2),
                overhead_percent = 10m,
                overhead_cost = Math.Round(overheadCost, 2),
                base_cost = Math.Round(baseCost, 2),
                is_rush = isRush,
                rush_percent = rushPercent,
                rush_amount = Math.Round(rushAmount, 2),
                system_total_cost = Math.Round(systemTotalCost, 2),
                manual_adjust_cost = 0m,
                final_total_cost = Math.Round(systemTotalCost, 2),
                cost_note = null,
                estimated_finish_date = DateTime.SpecifyKind(estimatedFinish, DateTimeKind.Unspecified),
                desired_delivery_date = DateTime.SpecifyKind(req.desired_delivery_date, DateTimeKind.Unspecified),
                created_at = DateTime.SpecifyKind(now, DateTimeKind.Unspecified),
                sheets_required = req.paper.sheets_base,
                sheets_waste = req.paper.total_waste,
                sheets_total = req.paper.sheets_with_waste,
                total_area_m2 = Math.Round(totalAreaM2, 4)
            };

            await _estimateRepo.AddAsync(entity);
            await _estimateRepo.SaveChangesAsync();

            // Trả response
            return new CostEstimateResponse
            {
                paper_cost = Math.Round(totalPaperCost, 2),
                ink_cost = Math.Round(inkCost, 2),
                coating_glue_cost = Math.Round(coatingGlueCost, 2),
                mounting_glue_cost = Math.Round(mountingGlueCost, 2),
                lamination_cost = Math.Round(laminationCost, 2),
                material_cost = Math.Round(materialCost, 2),
                overhead_cost = Math.Round(overheadCost, 2),
                base_cost = Math.Round(baseCost, 2),
                is_rush = isRush,
                rush_percent = rushPercent,
                rush_amount = Math.Round(rushAmount, 2),
                system_total_cost = Math.Round(systemTotalCost, 2),
                estimated_finish_date = DateTime.SpecifyKind(estimatedFinish, DateTimeKind.Utc)
            };
        }

        private decimal GetInkRate(ProductTypeCode productType)
        {
            return productType switch
            {
                ProductTypeCode.GACH_1MAU => INK_RATE_GACH_NOI_DIA,
                ProductTypeCode.GACH_XUAT_KHAU_DON_GIAN => INK_RATE_GACH_NOI_DIA,
                ProductTypeCode.GACH_XUAT_KHAU_TERACON => INK_RATE_GACH_NHIEU_MAU,
                ProductTypeCode.GACH_NOI_DIA_4SP => INK_RATE_GACH_NHIEU_MAU,
                ProductTypeCode.GACH_NOI_DIA_6SP => INK_RATE_GACH_NHIEU_MAU,
                _ => INK_RATE_HOP_MAU
            };
        }

        public async Task AdjustManualCostAsync(int estimateId, decimal adjustCost, string? note)
        {
            var estimate = await _estimateRepo.GetByIdAsync(estimateId)
                ?? throw new Exception("Estimate not found");

            estimate.manual_adjust_cost = adjustCost;
            estimate.final_total_cost = adjustCost + (estimate.rush_amount);
            estimate.cost_note = note;

            await _estimateRepo.SaveChangesAsync();
        }

    }
}