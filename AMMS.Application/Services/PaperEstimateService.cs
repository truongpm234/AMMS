using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class PaperEstimateService : IPaperEstimateService
    {
        private readonly IMaterialRepository _materialRepo;
        public PaperEstimateService(IMaterialRepository materialRepo) => _materialRepo = materialRepo;

        public async Task<PaperEstimateResponse> EstimatePaperAsync(PaperEstimateRequest req)
        {
            if (req.quantity <= 0) throw new ArgumentException("quantity must be > 0");
            if (req.length_mm <= 0 || req.width_mm <= 0 || req.height_mm <= 0)
                throw new ArgumentException("length_mm/width_mm/height_mm must be > 0");

            var paper = await _materialRepo.GetByCodeAsync(req.paper_code)
                       ?? throw new KeyNotFoundException($"Paper not found: {req.paper_code}");

            if (paper.sheet_width_mm is null || paper.sheet_height_mm is null)
                throw new InvalidOperationException("Missing sheet size in materials (sheet_width_mm/sheet_height_mm).");

            int sheetW = paper.sheet_width_mm.Value;
            int sheetH = paper.sheet_height_mm.Value;

            // ✅ Suy ra print size từ kích thước sản phẩm (hộp cơ bản)
            // (Bạn có thể thay công thức theo từng loại sản phẩm sau)
            int printW = 2 * (req.length_mm + req.width_mm) + req.allowance_mm + 2 * req.bleed_mm;
            int printH = (req.height_mm + req.width_mm) + req.allowance_mm + 2 * req.bleed_mm;

            if (printW > sheetW && printH > sheetH && printW > sheetH && printH > sheetW)
                throw new InvalidOperationException("Print size is larger than paper sheet in both orientations.");

            // ✅ n-up: thử 2 chiều xoay
            int n1 = (sheetW / printW) * (sheetH / printH);
            int n2 = (sheetW / printH) * (sheetH / printW);
            int nUp = Math.Max(Math.Max(n1, n2), 1);

            int sheetsBase = (int)Math.Ceiling(req.quantity / (decimal)nUp);
            int sheetsWithWaste = (int)Math.Ceiling(sheetsBase * (1m + req.wastage_percent / 100m));

            decimal? costPerSheet = paper.cost_price;
            decimal? totalPaperCost = costPerSheet is null ? null : costPerSheet.Value * sheetsWithWaste;

            return new PaperEstimateResponse
            {
                paper_code = paper.code,
                sheet_width_mm = sheetW,
                sheet_height_mm = sheetH,
                print_width_mm = printW,
                print_height_mm = printH,
                n_up = nUp,
                sheets_base = sheetsBase,
                sheets_with_waste = sheetsWithWaste,
                waste_percent = req.wastage_percent

            };
        }
    }
}
