using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Estimates.AMMS.Shared.DTOs.Estimates;

namespace AMMS.Application.Services
{
    public class EstimateService : IEstimateService
    {
        private readonly IMaterialRepository _materialRepo;
        private readonly ICostEstimateRepository _estimateRepo;

        // Định mức vật liệu
        private const decimal INK_RATE_GACH_NOI_DIA = 0.0003m;
        private const decimal INK_RATE_HOP_MAU = 0.0009m;
        private const decimal INK_RATE_GACH_NHIEU_MAU = 0.001m;
        private const decimal COATING_GLUE_RATE = 0.004m;
        private const decimal MOUNTING_GLUE_RATE = 0.004m;
        private const decimal LAMINATION_RATE = 0.017m;

        // Giá vật liệu (VND)
        private const decimal INK_PRICE_PER_KG = 150000m;
        private const decimal COATING_GLUE_PRICE_PER_KG = 80000m;
        private const decimal MOUNTING_GLUE_PRICE_PER_KG = 60000m;
        private const decimal LAMINATION_PRICE_PER_KG = 200000m;

        public EstimateService(
            IMaterialRepository materialRepo,
            ICostEstimateRepository costEstimateRepository)
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
            // =====================
            // 1. VALIDATION
            // =====================
            if (string.IsNullOrWhiteSpace(req.paper_code))
                throw new ArgumentException("paper_code is required");

            if (req.quantity <= 0)
                throw new ArgumentException("quantity must be > 0");

            if (req.length_mm <= 0 || req.width_mm <= 0 || req.height_mm <= 0)
                throw new ArgumentException("length_mm, width_mm, height_mm must be > 0");

            if (req.bleed_mm < 0)
                throw new ArgumentException("bleed_mm must be >= 0");

            // =====================
            // 2. LẤY THÔNG TIN GIẤY
            // =====================
            var paper = await _materialRepo.GetByCodeAsync(req.paper_code)
                ?? throw new KeyNotFoundException($"Paper not found: {req.paper_code}");

            if (paper.sheet_width_mm is null || paper.sheet_height_mm is null)
                throw new InvalidOperationException("Missing sheet size in materials.");

            int sheetW = paper.sheet_width_mm.Value;
            int sheetH = paper.sheet_height_mm.Value;

            // =====================
            // 3. TÍNH KÍCH THƯỚC TRIỂN KHAI HỘP 2D
            // =====================

            // Tab dán (glue flap) - mặc định 20mm nếu không được chỉ định
            int tabWidth = req.glue_tab_mm > 0 ? req.glue_tab_mm : 20;

            int printW, printH;

            // Xác định loại sản phẩm
            var productType = ParseEnum<ProductTypeCode>(req.product_type, "product_type");
            bool isBoxProduct = productType.ToString().StartsWith("HOP_MAU");
            bool isBrickProduct = productType.ToString().StartsWith("GACH_");

            if (isBrickProduct)
            {
                // HỘP GẠCH (dạng tray đơn giản)
                // Triển khai: L + 2H (2 cạnh) × W + 2H (2 cạnh)
                printW = req.length_mm + 2 * req.height_mm + 2 * req.bleed_mm;
                printH = req.width_mm + 2 * req.height_mm + 2 * req.bleed_mm;
            }
            else if (isBoxProduct)
            {
                // HỘP CARTON CHUẨN

                // Chiều NGANG: Chu vi hộp (2L + 2W) + tab dán + bleed 2 bên
                printW = 2 * (req.length_mm + req.width_mm)
                         + tabWidth
                         + 2 * req.bleed_mm;

                // Chiều DỌC: 
                // - Hộp 2 chiều (cơ bản): Đáy + Thân + Nắp = 2W + H
                // - Hộp 1 chiều: chỉ Thân + Nắp/Đáy = W + H
                if (req.is_one_side_box)
                {
                    printH = req.width_mm + req.height_mm + 2 * req.bleed_mm;
                }
                else
                {
                    // CÔNG THỨC CHÍNH XÁC cho hộp cơ bản
                    printH = (2 * req.width_mm + req.height_mm) + 2 * req.bleed_mm;
                }
            }
            else
            {
                // Sản phẩm khác - dùng công thức mặc định
                printW = 2 * (req.length_mm + req.width_mm) + tabWidth + 2 * req.bleed_mm;
                printH = (2 * req.width_mm + req.height_mm) + 2 * req.bleed_mm;
            }

            // =====================
            // 4. TÍNH N-UP (2D)
            // =====================

            // Thử 2 cách bố trí
            int n1 = (sheetW / printW) * (sheetH / printH); // không xoay
            int n2 = (sheetW / printH) * (sheetH / printW); // xoay 90°

            int nUp = Math.Max(n1, n2);

            if (nUp <= 0)
            {
                throw new InvalidOperationException(
                    $"Print size ({printW}×{printH}mm) does not fit paper sheet ({sheetW}×{sheetH}mm). " +
                    $"Please check product dimensions or use larger paper."
                );
            }

            // =====================
            // 5. TÍNH SỐ TỜ CƠ BẢN
            // =====================

            int sheetsBase = (int)Math.Ceiling(req.quantity / (decimal)nUp);

            // =====================
            // 6. TÍNH HAO HỤT THEO CÔNG ĐOẠN
            // =====================

            var coatingType = ParseEnum<CoatingType>(
                req.coating_type ?? CoatingType.NONE.ToString(),
                "coating_type"
            );

            var processes = req.production_processes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => ParseEnum<ProcessType>(p.Trim(), "process"))
                .ToList();

            int wastePrinting = CalculatePrintingWaste(productType, req.number_of_plates);

            int wasteDieCutting = processes.Contains(ProcessType.BE)
                ? CalculateProcessWaste(sheetsBase, ProcessType.BE)
                : 0;

            int wasteMounting = processes.Contains(ProcessType.BOI)
                ? CalculateProcessWaste(sheetsBase, ProcessType.BOI)
                : 0;

            int wasteCoating = processes.Contains(ProcessType.PHU)
                ? CalculateCoatingWaste(sheetsBase, coatingType)
                : 0;

            int wasteLamination = processes.Contains(ProcessType.CAN_MANG)
                ? CalculateLaminationWaste(sheetsBase)
                : 0;

            int wasteGluing = processes.Contains(ProcessType.DAN)
                ? CalculateGluingWaste(req.quantity)
                : 0;

            int totalWaste = wastePrinting + wasteDieCutting + wasteMounting
                           + wasteCoating + wasteLamination + wasteGluing;

            int sheetsWithWaste = sheetsBase + totalWaste;

            decimal wastePercent = sheetsBase > 0
                ? (totalWaste / (decimal)sheetsBase) * 100m
                : 0m;

            // =====================
            // 7. TRẢ KẾT QUẢ
            // =====================

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

            // Thêm 10 tờ cho mỗi cao bản (chỉ áp dụng cho hộp màu)
            if (productType.ToString().StartsWith("HOP_MAU") && numberOfPlates > 0)
            {
                baseWaste += numberOfPlates * 10;
            }

            return baseWaste;
        }

        private int CalculateProcessWaste(int sheets, ProcessType processType)
        {
            // Áp dụng cho BẾ và BỒI
            return sheets switch
            {
                < 5000 => 20,
                < 20000 => 30,
                <= 40000 => 40,
                _ => 40
            };
        }

        private int CalculateCoatingWaste(int sheets, CoatingType coatingType)
        {
            if (coatingType == CoatingType.KEO_NUOC)
                return 0; // Keo nước không tính hao hụt

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
            return 25; // Trung bình 20-30 hộp
        }

        public async Task<CostEstimateResponse> CalculateCostEstimateAsync(CostEstimateRequest req)
        {
            if (req.paper == null)
                throw new ArgumentException("Paper information is required");

            if (req.paper.sheets_with_waste <= 0)
                throw new ArgumentException("Sheets with waste must be greater than 0");

            var paper = await _materialRepo.GetByCodeAsync(req.paper.paper_code)
                ?? throw new KeyNotFoundException($"Paper not found");

            if (paper.cost_price == null)
                throw new InvalidOperationException($"Paper cost_price missing for {paper.code}");

            // Tính diện tích (m2)
            decimal sheetAreaM2 = (req.paper.sheet_width_mm / 1000m)
                                * (req.paper.sheet_height_mm / 1000m);
            decimal totalAreaM2 = sheetAreaM2 * req.paper.sheets_with_waste;

            // Chi phí giấy
            decimal paperCostPerSheet = paper.cost_price.Value;
            decimal totalPaperCost = req.paper.sheets_with_waste * paperCostPerSheet;

            // Chi phí mực in
            var productType = ParseEnum<ProductTypeCode>(req.product_type, "product_type");
            decimal inkRate = GetInkRate(productType);
            decimal inkWeightKg = totalAreaM2 * inkRate;
            decimal inkCost = inkWeightKg * INK_PRICE_PER_KG;

            // Chi phí keo phủ
            decimal coatingGlueCost = 0m;
            var coatingType = ParseEnum<CoatingType>(
                req.coating_type ?? "NONE",
                "coating_type"
            );

            var processes = req.production_processes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
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

            // Chi phí keo bồi
            decimal mountingGlueCost = 0m;
            if (processes.Contains(ProcessType.BOI))
            {
                decimal mountingGlueWeightKg = totalAreaM2 * MOUNTING_GLUE_RATE;
                mountingGlueCost = mountingGlueWeightKg * MOUNTING_GLUE_PRICE_PER_KG;
            }

            // Chi phí màng
            decimal laminationCost = 0m;
            if (processes.Contains(ProcessType.CAN_MANG) || req.has_lamination)
            {
                decimal laminationWeightKg = totalAreaM2 * LAMINATION_RATE;
                laminationCost = laminationWeightKg * LAMINATION_PRICE_PER_KG;
            }

            // Tổng chi phí vật liệu
            decimal materialCost = totalPaperCost + inkCost + coatingGlueCost
                                 + mountingGlueCost + laminationCost;

            // Khấu hao 10%
            decimal overheadCost = materialCost * 0.10m;
            decimal baseCost = materialCost + overheadCost;

            // Ngày hoàn thành dự kiến
            var now = DateTime.UtcNow;
            var estimatedFinish = now.AddDays(5);

            // Rush order
            bool isRush = false;
            decimal rushPercent = 0m;

            if (req.desired_delivery_date < estimatedFinish)
            {
                int daysEarly = (int)(estimatedFinish - req.desired_delivery_date).TotalDays;

                rushPercent = daysEarly switch
                {
                    1 => 5m,
                    >= 2 and <= 3 => 20m,
                    >= 4 => 40m,
                    _ => 0m
                };

                isRush = rushPercent > 0;
            }

            decimal rushAmount = baseCost * rushPercent / 100;
            decimal systemTotalCost = baseCost + rushAmount;

            // Lưu database
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
            estimate.final_total_cost = adjustCost + estimate.rush_amount;
            estimate.cost_note = note;

            await _estimateRepo.SaveChangesAsync();
        }
    }
}
