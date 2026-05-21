using AMMS.Infrastructure.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AMMS.Application.Helpers
{
    public static class SubProductImportReceiptPdfHelper
    {
        public static byte[] GeneratePdf(IReadOnlyList<sub_product> items)
        {
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("Không có bán thành phẩm để tạo phiếu nhập.");

            var now = DateTime.Now;
            var receiptNo = $"BTP-IN-{now:yyyyMMdd-HHmmss}";
            var totalQty = items.Sum(x => x.quantity);

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

                    page.Header().Element(c => ComposeHeader(c, receiptNo, now));

                    page.Content().Element(c => ComposeContent(c, items, totalQty));

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
            DateTime now)
        {
            container.Column(column =>
            {
                column.Spacing(6);

                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        left.Item().Text("AMMS - PHIẾU NHẬP BÁN THÀNH PHẨM")
                            .Bold()
                            .FontSize(15);

                        left.Item().Text("Phiếu được tạo tự động từ hệ thống sản xuất.")
                            .FontSize(9)
                            .FontColor(Colors.Grey.Darken1);
                    });

                    row.ConstantItem(180).AlignRight().Column(right =>
                    {
                        right.Item().Text($"Số phiếu: {receiptNo}")
                            .Bold();

                        right.Item().Text($"Ngày tạo: {now:dd/MM/yyyy HH:mm}");
                    });
                });

                column.Item().LineHorizontal(1);
            });
        }

        private static void ComposeContent(
            IContainer container,
            IReadOnlyList<sub_product> items,
            int totalQty)
        {
            container.Column(column =>
            {
                column.Spacing(12);

                column.Item().Text("DANH SÁCH BÁN THÀNH PHẨM NHẬP KHO")
                    .Bold()
                    .FontSize(12);

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(32);
                        columns.ConstantColumn(55);
                        columns.RelativeColumn(1.2f);
                        columns.ConstantColumn(55);
                        columns.ConstantColumn(55);
                        columns.ConstantColumn(60);
                        columns.ConstantColumn(60);
                        columns.RelativeColumn(1.2f);
                    });

                    AddHeader(table, "STT");
                    AddHeader(table, "Sub ID");
                    AddHeader(table, "Loại SP");
                    AddHeader(table, "Width");
                    AddHeader(table, "Length");
                    AddHeader(table, "Công đoạn");
                    AddHeader(table, "SL");
                    AddHeader(table, "Nguồn");

                    var index = 1;

                    foreach (var sp in items.OrderBy(x => x.id))
                    {
                        AddCell(table, index.ToString());
                        AddCell(table, sp.id.ToString());
                        AddCell(table, sp.product_type?.name ?? $"PT #{sp.product_type_id}");
                        AddCell(table, sp.width?.ToString() ?? "-");
                        AddCell(table, sp.length?.ToString() ?? "-");
                        AddCell(table, sp.product_process ?? "-");
                        AddCell(table, sp.quantity.ToString("N0"));
                        AddCell(table, BuildSourceText(sp));

                        index++;
                    }
                });

                column.Item().PaddingTop(6).Row(row =>
                {
                    row.RelativeItem().Text($"Tổng dòng: {items.Count}")
                        .Bold();

                    row.RelativeItem().AlignRight().Text($"Tổng số lượng: {totalQty:N0}")
                        .Bold();
                });

                column.Item().PaddingTop(18).Text("XÁC NHẬN")
                    .Bold()
                    .FontSize(12);

                column.Item().Row(row =>
                {
                    row.RelativeItem().AlignCenter().Column(col =>
                    {
                        col.Item().Text("Người lập phiếu").Bold();
                        col.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                        col.Item().PaddingTop(60).Text("");
                    });

                    row.RelativeItem().AlignCenter().Column(col =>
                    {
                        col.Item().Text("Thủ kho").Bold();
                        col.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                        col.Item().PaddingTop(60).Text("");
                    });

                    row.RelativeItem().AlignCenter().Column(col =>
                    {
                        col.Item().Text("Quản lý sản xuất").Bold();
                        col.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                        col.Item().PaddingTop(60).Text("");
                    });
                });
            });
        }

        private static string BuildSourceText(sub_product sp)
        {
            var parts = new List<string>();

            if (sp.source_order_id.HasValue)
                parts.Add($"Order {sp.source_order_id}");

            if (sp.source_prod_id.HasValue)
                parts.Add($"Prod {sp.source_prod_id}");

            if (sp.source_task_id.HasValue)
                parts.Add($"Task {sp.source_task_id}");

            return parts.Count == 0 ? "-" : string.Join(" / ", parts);
        }

        private static void AddHeader(TableDescriptor table, string text)
        {
            table.Cell()
                .Element(HeaderCell)
                .Text(text)
                .Bold();
        }

        private static void AddCell(TableDescriptor table, string text)
        {
            table.Cell()
                .Element(BodyCell)
                .Text(text);
        }

        private static IContainer HeaderCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Background(Colors.Grey.Lighten3)
                .PaddingVertical(5)
                .PaddingHorizontal(4)
                .AlignCenter();
        }

        private static IContainer BodyCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .PaddingVertical(5)
                .PaddingHorizontal(4);
        }
    }
}