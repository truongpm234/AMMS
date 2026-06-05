using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

using AMMS.Shared.DTOs.Productions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace AMMS.Application.Helpers
{
    public static class ImportReceivePdfHelper
    {
        public static void Generate(
            string outputPath,
            List<ImportReceiveSourceDto> sources)
        {
            sources ??= new List<ImportReceiveSourceDto>();

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("outputPath không hợp lệ.");

            if (sources.Count == 0)
                throw new InvalidOperationException("Không có dữ liệu để tạo phiếu nhập kho.");

            var now = AppTime.NowVnUnspecified();
            var receiptNo = $"PNK-{now:yyyyMMddHHmmss}";

            /*
             * FIX CHÍNH:
             * Group theo order_id.
             * Trước đây nếu sources có 3 production của cùng 1 order thì PDF dễ bị 3 dòng.
             * Bây giờ 1 order chỉ còn 1 dòng.
             */
            var orderRows = sources
                .GroupBy(x => new
                {
                    x.order_id,
                    order_code = x.order_code ?? ""
                })
                .Select(g =>
                {
                    var prodIds = g
                        .Select(x => x.prod_id)
                        .Where(x => x > 0)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToList();

                    /*
                     * Vì cùng một order có thể bị lặp items ở nhiều production,
                     * phải distinct theo item_id để không nhân số lượng.
                     */
                    var uniqueItems = g
                        .SelectMany(x => x.items ?? new List<ImportReceiveItemDto>())
                        .GroupBy(x => x.item_id > 0
                            ? $"ID={x.item_id}"
                            : $"NAME={x.product_name}|QTY={x.quantity}|PACK={x.packaging_standard}")
                        .Select(x => x.First())
                        .OrderBy(x => x.item_id)
                        .ToList();

                    var productNames = uniqueItems.Count == 0
                        ? ""
                        : string.Join("; ",
                            uniqueItems
                                .Select(x => x.product_name)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase));

                    var packagingStandards = uniqueItems.Count == 0
                        ? ""
                        : string.Join("; ",
                            uniqueItems
                                .Select(x => x.packaging_standard)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase));

                    var totalQuantity = uniqueItems.Sum(x => x.quantity);

                    return new ImportReceiveOrderRow
                    {
                        order_id = g.Key.order_id,
                        order_code = g.Key.order_code,

                        prod_ids = prodIds,

                        product_names = productNames,
                        packaging_standard = packagingStandards,
                        total_quantity = totalQuantity,

                        item_count = uniqueItems.Count
                    };
                })
                .OrderBy(x => x.order_id)
                .ToList();

            if (orderRows.Count == 0)
                throw new InvalidOperationException("Không có dòng order hợp lệ để tạo phiếu nhập kho.");

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(24);
                    page.PageColor(Colors.White);

                    page.DefaultTextStyle(x => x
                        .FontFamily("DejaVu Sans")
                        .FontSize(9));

                    page.Content().Column(col =>
                    {
                        col.Spacing(10);

                        col.Item().AlignCenter()
                            .Text("PHIẾU NHẬP KHO THÀNH PHẨM")
                            .Bold()
                            .FontSize(18);

                        col.Item().AlignCenter()
                            .Text($"Số phiếu: {receiptNo} - Ngày tạo: {now:dd/MM/yyyy HH:mm}");

                        col.Item().Text(
                            "Ghi chú: Không.")
                            .Italic()
                            .FontSize(8);

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(35);
                                c.ConstantColumn(70);
                                c.ConstantColumn(105);
                                c.RelativeColumn(1.2f);
                                c.RelativeColumn(2.0f);
                                c.ConstantColumn(80);
                                c.RelativeColumn(1.4f);
                                c.RelativeColumn(1.2f);
                            });

                            Header(table, "STT");
                            Header(table, "Order ID");
                            Header(table, "Mã đơn");
                            Header(table, "Production liên quan");
                            Header(table, "Sản phẩm");
                            Header(table, "Tổng SL");
                            Header(table, "Quy cách");
                            Header(table, "Ghi chú");

                            var index = 1;

                            foreach (var row in orderRows)
                            {
                                Cell(table, index.ToString(CultureInfo.InvariantCulture));
                                Cell(table, row.order_id.ToString(CultureInfo.InvariantCulture));
                                Cell(table, row.order_code ?? "");
                                Cell(table, row.prod_ids.Count == 0
                                    ? ""
                                    : string.Join(", ", row.prod_ids));

                                Cell(table, row.product_names ?? "");
                                Cell(table, FormatQty(row.total_quantity));
                                Cell(table, row.packaging_standard ?? "");

                                Cell(table,
                                    row.item_count <= 1
                                        ? "Yêu cầu nhập kho từ quản lí tổng hợp."
                                        : "Yêu cầu nhập kho từ quản lí tổng hợp.");

                                index++;
                            }
                        });

                        var totalOrders = orderRows.Count;
                        var totalQty = orderRows.Sum(x => x.total_quantity);

                        col.Item().AlignRight()
                            .Text($"Tổng số order: {totalOrders} | Tổng số lượng: {FormatQty(totalQty)}")
                            .Bold();
                    });
                });
            }).GeneratePdf(outputPath);
        }

        private sealed class ImportReceiveOrderRow
        {
            public int order_id { get; set; }

            public string? order_code { get; set; }

            public List<int> prod_ids { get; set; } = new();

            public string? product_names { get; set; }

            public string? packaging_standard { get; set; }

            public int total_quantity { get; set; }

            public int item_count { get; set; }
        }

        private static void Header(TableDescriptor table, string text)
        {
            table.Cell()
                .Border(0.5f)
                .Background(Colors.Grey.Lighten2)
                .Padding(4)
                .Text(text ?? "")
                .SemiBold();
        }

        private static void Cell(TableDescriptor table, string text)
        {
            table.Cell()
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Padding(4)
                .Text(text ?? "");
        }

        private static string FormatQty(int value)
            => string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);
    }
}