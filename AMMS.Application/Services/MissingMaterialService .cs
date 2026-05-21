using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AMMS.Application.Services
{
    public class MissingMaterialService : IMissingMaterialService
    {
        private readonly IMissingMaterialRepository _repo;

        public MissingMaterialService(IMissingMaterialRepository repo)
        {
            _repo = repo;
        }

        public async Task<PagedResultLite<MissingMaterialDto>> GetPagedAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            await _repo.RecalculateAndSaveAsync(ct);

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
            var data = result.Data ?? new List<MissingMaterialDto>();

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Missing Materials");

            var headers = new[]
            {
                "Mã NVL",
                "Tên nguyên vật liệu",
                "Đơn vị",
                "Ngày cần",
                "Cần dùng",
                "Tồn kho",
                "Số lượng cần nhập",
                "Tổng tiền dự kiến"
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
                ws.Cell(row, 1).Value = item.material_id;
                ws.Cell(row, 2).Value = item.material_name;
                ws.Cell(row, 3).Value = item.unit;
                ws.Cell(row, 4).Value = item.request_date?.ToString("dd/MM/yyyy") ?? "";

                ws.Cell(row, 5).Value = item.needed;
                ws.Cell(row, 6).Value = item.available;
                ws.Cell(row, 7).Value = item.quantity;
                ws.Cell(row, 8).Value = item.total_price;

                row++;
            }

            if (row > 2)
            {
                var dataRange = ws.Range(2, 1, row - 1, headers.Length);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            ws.Column(1).Width = 12;
            ws.Column(2).Width = 35;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 15;
            ws.Column(5).Width = 15;
            ws.Column(6).Width = 15;
            ws.Column(7).Width = 20;
            ws.Column(8).Width = 20;
            ws.Column(9).Width = 15;

            ws.Column(5).Style.NumberFormat.Format = "#,##0.####";
            ws.Column(6).Style.NumberFormat.Format = "#,##0.####";
            ws.Column(7).Style.NumberFormat.Format = "#,##0.####";
            ws.Column(8).Style.NumberFormat.Format = "#,##0";

            ws.SheetView.FreezeRows(1);
            ws.RangeUsed()?.SetAutoFilter();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return stream.ToArray();
        }
    }
}