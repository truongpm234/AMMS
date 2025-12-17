using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;

namespace AMMS.Application.Services
{
    public class PaperEstimateService : IEstimateService
    {
        private readonly IMaterialRepository _materialRepo;
        private const decimal DEFAULT_WASTE_PERCENT = 5m;

        public PaperEstimateService(IMaterialRepository materialRepo) => _materialRepo = materialRepo;

        public async Task<PaperEstimateResponse> EstimatePaperAsync(PaperEstimateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.paper_code))
                throw new ArgumentException("paper_code is required");

            if (req.quantity <= 0)
                throw new ArgumentException("quantity must be > 0");

            if (req.length_mm <= 0 || req.width_mm <= 0 || req.height_mm <= 0)
                throw new ArgumentException("length_mm/width_mm/height_mm must be > 0");

            if (req.allowance_mm < 0 || req.bleed_mm < 0)
                throw new ArgumentException("allowance_mm/bleed_mm must be >= 0");

            var paper = await _materialRepo.GetByCodeAsync(req.paper_code)
                       ?? throw new KeyNotFoundException($"Paper not found: {req.paper_code}");

            if (paper.sheet_width_mm is null || paper.sheet_height_mm is null)
                throw new InvalidOperationException("Missing sheet size in materials (sheet_width_mm/sheet_height_mm).");

            int sheetW = paper.sheet_width_mm.Value;
            int sheetH = paper.sheet_height_mm.Value;

            // print size (hộp cơ bản)
            int printW = 2 * (req.length_mm + req.width_mm) + req.allowance_mm + 2 * req.bleed_mm;
            int printH = (req.height_mm + req.width_mm) + req.allowance_mm + 2 * req.bleed_mm;

            bool fitNormal = printW <= sheetW && printH <= sheetH;
            bool fitRotate = printH <= sheetW && printW <= sheetH;
            if (!fitNormal && !fitRotate)
                throw new InvalidOperationException("Print size is larger than paper sheet in both orientations.");

            int n1 = (sheetW / printW) * (sheetH / printH);
            int n2 = (sheetW / printH) * (sheetH / printW);
            int nUp = Math.Max(Math.Max(n1, n2), 1);

            int sheetsBase = (int)Math.Ceiling(req.quantity / (decimal)nUp);

            // ✅ luôn 5%
            var waste = DEFAULT_WASTE_PERCENT;
            int sheetsWithWaste = (int)Math.Ceiling(sheetsBase * (1m + waste / 100m));

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
                sheets_with_waste = sheetsWithWaste,
                waste_percent = waste
            };
        }
    }
}
