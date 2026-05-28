using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Spreadsheet;
using AMMS.Application.Helpers;

namespace AMMS.Application.Services
{
    public class MissingMaterialService : IMissingMaterialService
    {
        private readonly IMissingMaterialRepository _repo;
        private readonly ICloudinaryFileStorageService _fileStorage;

        public MissingMaterialService(
            IMissingMaterialRepository repo,
            ICloudinaryFileStorageService fileStorage)
        {
            _repo = repo;
            _fileStorage = fileStorage;
        }

        public async Task<PagedResultLite<MissingMaterialDto>> GetPagedAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            //await _repo.RecalculateAndSaveAsync(ct);

            var result = await _repo.GetPagedFromDbAsync(page, pageSize, ct);
            if (result.Data == null || result.Data.Count == 0)
                return result;

            static decimal RoundUpToHundreds(decimal value)
            {
                if (value <= 0m)
                    return 0m;

                return Math.Ceiling(value / 100m) * 100m;
            }

            foreach (var x in result.Data)
            {
                var baseQty = x.quantity;

                if (baseQty < 0m)
                    baseQty = 0m;

                var roundedQty = RoundUpToHundreds(baseQty);

                decimal unitPrice = 0m;

                if (baseQty > 0m && x.total_price > 0m)
                    unitPrice = x.total_price / baseQty;

                x.quantity = roundedQty;
                x.total_price = Math.Round(roundedQty * unitPrice, 2);
            }

            return result;
        }

        public async Task<byte[]> ExportExcelAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            var result = await GetPagedAsync(page, pageSize, ct);

            var data = (result.Data ?? new List<MissingMaterialDto>())
    .Where(x =>
        x.is_buy == false &&
        x.is_active == true &&
        HasPurchasePdf(x.file_purpose))
    .ToList();

            if (data.Count == 0)
            {
                throw new InvalidOperationException(
                    "Không có dòng hợp lệ để export Excel");
            }

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Missing Materials");

            var headers = new[]
            {
        "Miss ID",
        "Mã NVL",
        "Tên nguyên vật liệu",
        "Đơn vị",
        "Ngày cần",
        "Cần dùng",
        "Tồn kho",
        "Số lượng cần nhập",
        "Tổng tiền dự kiến",
        "Link PDF mua NVL"
    };

            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            var headerRange = ws.Range(1, 1, 1, headers.Length);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightBlue;
            headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            var row = 2;

            foreach (var item in data)
            {
                ws.Cell(row, 1).Value = item.miss_id;
                ws.Cell(row, 2).Value = item.material_id;
                ws.Cell(row, 3).Value = item.material_name;
                ws.Cell(row, 4).Value = item.unit;
                ws.Cell(row, 5).Value = item.request_date?.ToString("dd/MM/yyyy") ?? "";

                ws.Cell(row, 6).Value = item.needed;
                ws.Cell(row, 7).Value = item.available;
                ws.Cell(row, 8).Value = item.quantity;
                ws.Cell(row, 9).Value = item.total_price;
                ws.Cell(row, 10).Value = item.file_purpose ?? "";

                row++;
            }

            if (row > 2)
            {
                var dataRange = ws.Range(2, 1, row - 1, headers.Length);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            ws.Column(1).Width = 12;
            ws.Column(2).Width = 12;
            ws.Column(3).Width = 35;
            ws.Column(4).Width = 12;
            ws.Column(5).Width = 15;
            ws.Column(6).Width = 15;
            ws.Column(7).Width = 15;
            ws.Column(8).Width = 20;
            ws.Column(9).Width = 20;
            ws.Column(10).Width = 70;

            ws.Column(6).Style.NumberFormat.Format = "#,##0.####";
            ws.Column(7).Style.NumberFormat.Format = "#,##0.####";
            ws.Column(8).Style.NumberFormat.Format = "#,##0.####";
            ws.Column(9).Style.NumberFormat.Format = "#,##0";

            ws.SheetView.FreezeRows(1);
            ws.RangeUsed()?.SetAutoFilter();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return stream.ToArray();
        }

        private static bool HasPurchasePdf(string? filePurpose)
        {
            if (string.IsNullOrWhiteSpace(filePurpose))
                return false;

            var value = filePurpose.Trim();

            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<GenerateMissingMaterialPurchasePdfResponse> GeneratePurchasePdfAsync(
    List<long> missIds,
    CancellationToken ct = default)
        {
            missIds = missIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<long>();

            if (missIds.Count == 0)
                throw new InvalidOperationException("Vui lòng chọn ít nhất một miss_id để tạo file mua nguyên vật liệu.");

            // Không gọi RecalculateAndSaveAsync ở đây.
            // Vì recalculate sẽ xóa bảng missing_materials và tạo lại miss_id mới.
            var rows = await _repo.GetPurchasePdfRowsByMissIdsAsync(
                missIds,
                ct);

            if (rows == null || rows.Count == 0)
                throw new InvalidOperationException("Không tìm thấy nguyên vật liệu thiếu hợp lệ theo miss_id đã chọn.");

            var foundMissIds = rows
                .Select(x => x.miss_id)
                .Distinct()
                .ToHashSet();

            var notFoundMissIds = missIds
                .Where(x => !foundMissIds.Contains(x))
                .ToList();

            var fileName = $"material-purchase-selected-{DateTime.Now:yyyyMMddHHmmss}.pdf";

            var tempFolder = Path.Combine(Path.GetTempPath(), "amms-material-purchases");
            Directory.CreateDirectory(tempFolder);

            var tempFilePath = Path.Combine(tempFolder, fileName);

            MissingMaterialPurchasePdfHelper.Generate(
                tempFilePath,
                rows);

            string fileUrl;

            await using (var fs = new FileStream(
                tempFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var publicId = $"material-purchases/{Path.GetFileNameWithoutExtension(fileName)}";

                fileUrl = await _fileStorage.UploadRawWithPublicIdAsync(
                    fs,
                    fileName,
                    "application/pdf",
                    publicId);
            }

            var updatedRows = await _repo.UpdateFilePurposeByMissIdsAsync(
                rows.Select(x => x.miss_id).ToList(),
                fileUrl,
                ct);

            try
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
            catch
            {
            }

            var message = notFoundMissIds.Count == 0
                ? "Tạo file mua nguyên vật liệu theo miss_id thành công."
                : $"Tạo file thành công. Một số miss_id không hợp lệ hoặc quantity <= 0: {string.Join(", ", notFoundMissIds)}";

            return new GenerateMissingMaterialPurchasePdfResponse
            {
                success = true,
                file_name = fileName,
                file_url = fileUrl,
                total_rows = rows.Count,
                total_amount = rows.Sum(x => x.total_price),
                updated_file_purpose_rows = updatedRows,
                message = message
            };
        }

        public async Task<object> RecalculateAsync(CancellationToken ct = default)
        {
            var result = await _repo.RecalculateAndSaveAsync(ct);
            return result;
        }
    }
}