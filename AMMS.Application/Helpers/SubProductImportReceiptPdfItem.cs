using AMMS.Infrastructure.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace AMMS.Application.Helpers
{
    public sealed class SubProductImportReceiptPdfItem
    {
        public int id { get; set; }

        public int product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public int? width { get; set; }

        public int? length { get; set; }

        public string? product_process { get; set; }

        public int quantity { get; set; }

        public int? source_order_id { get; set; }

        public int? source_prod_id { get; set; }

        public int? source_task_id { get; set; }

        public string? description { get; set; }
    }

    public static class SubProductImportReceiptPdfHelper
    {
        public static byte[] GeneratePdf(
            IReadOnlyList<SubProductImportReceiptPdfItem> items,
            string receiptNo,
            DateTime createdAt)
        {
            items ??= new List<SubProductImportReceiptPdfItem>();

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(28);
                    page.PageColor(Colors.White);

                    page.DefaultTextStyle(x => x
                        .FontFamily("DejaVu Sans")
                        .FontSize(9));

                    page.Header().Element(c => ComposeHeader(c, receiptNo, createdAt));
                    page.Content().Element(c => ComposeContent(c, items));
                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.Span("Trang ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            }).GeneratePdf();
        }

        private static void ComposeHeader(
            IContainer container,
            string receiptNo,
            DateTime createdAt)
        {
            container.Column(col =>
            {
                col.Spacing(4);

                col.Item().AlignCenter().Text("PHIẾU NHẬP KHO BÁN THÀNH PHẨM")
                    .Bold()
                    .FontSize(18);

                col.Item().AlignCenter().Text($"Số phiếu: {receiptNo}")
                    .SemiBold();

                col.Item().AlignCenter().Text(
                    $"Ngày {createdAt:dd/MM/yyyy HH:mm}");

                col.Item().PaddingTop(8).LineHorizontal(1);
            });
        }

        private static void ComposeContent(
            IContainer container,
            IReadOnlyList<SubProductImportReceiptPdfItem> items)
        {
            container.Column(col =>
            {
                col.Spacing(8);

                col.Item().Text("Nội dung nhập kho")
                    .Bold()
                    .FontSize(12);

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(28);   // STT
                        columns.ConstantColumn(48);   // ID
                        columns.RelativeColumn(1.4f); // Loại SP
                        columns.ConstantColumn(60);   // Kích thước
                        columns.RelativeColumn(1.3f); // Công đoạn
                        columns.ConstantColumn(58);   // SL
                        columns.ConstantColumn(62);   // Order
                        columns.ConstantColumn(62);   // Prod
                    });

                    Header(table, "STT");
                    Header(table, "Sub ID");
                    Header(table, "Loại SP");
                    Header(table, "Size");
                    Header(table, "BTP công đoạn");
                    Header(table, "SL");
                    Header(table, "Order");
                    Header(table, "Prod");

                    var index = 1;

                    foreach (var item in items)
                    {
                        Cell(table, index.ToString());
                        Cell(table, item.id.ToString());
                        Cell(table, item.product_type_name ?? $"PT-{item.product_type_id}");
                        Cell(table, BuildSize(item));
                        Cell(table, item.product_process ?? "");
                        Cell(table, FormatNumber(item.quantity), alignRight: true);
                        Cell(table, item.source_order_id?.ToString() ?? "");
                        Cell(table, item.source_prod_id?.ToString() ?? "");

                        index++;
                    }

                    table.Cell().ColumnSpan(5).Element(TotalCell).AlignRight().Text("Tổng số lượng").Bold();
                    table.Cell().Element(TotalCell).AlignRight().Text(FormatNumber(items.Sum(x => x.quantity))).Bold();
                    table.Cell().ColumnSpan(2).Element(TotalCell).Text("");
                });

                col.Item().PaddingTop(8).Text(
                    "Phiếu được tạo tự động từ hệ thống AMMS khi ghi nhận bán thành phẩm chờ nhập kho.")
                    .FontSize(8)
                    .Italic();
            });
        }

        private static string BuildSize(SubProductImportReceiptPdfItem item)
        {
            if (!item.width.HasValue && !item.length.HasValue)
                return "";

            return $"{item.width?.ToString() ?? "-"} x {item.length?.ToString() ?? "-"}";
        }

        private static string FormatNumber(decimal value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);
        }

        private static void Header(TableDescriptor table, string text)
        {
            table.Cell()
                .Element(HeaderCell)
                .Text(text)
                .Bold();
        }

        private static void Cell(TableDescriptor table, string text, bool alignRight = false)
        {
            var cell = table.Cell().Element(BodyCell);

            if (alignRight)
                cell.AlignRight().Text(text);
            else
                cell.Text(text);
        }

        private static IContainer HeaderCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Background(Colors.Grey.Lighten3)
                .PaddingVertical(5)
                .PaddingHorizontal(4);
        }

        private static IContainer BodyCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .PaddingVertical(4)
                .PaddingHorizontal(4);
        }

        private static IContainer TotalCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Background(Colors.Grey.Lighten4)
                .PaddingVertical(5)
                .PaddingHorizontal(4);
        }
    }
}