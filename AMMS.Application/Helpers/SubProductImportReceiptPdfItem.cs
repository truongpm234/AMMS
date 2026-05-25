using AMMS.Infrastructure.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace AMMS.Application.Helpers
{
    public static class SubProductImportReceiptPdfHelper
    {
        public static byte[] GeneratePdf(
            List<sub_product> items,
            string receiptNo,
            DateTime receiptDate)
        {
            return Document.Create(container =>
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

                        col.Item().AlignCenter().Text("PHIẾU YÊU CẦU NHẬP BÁN THÀNH PHẨM")
                            .Bold()
                            .FontSize(18);

                        col.Item().AlignCenter().Text(
                            $"Số phiếu: {receiptNo} - Ngày {receiptDate:dd/MM/yyyy HH:mm}");

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(35);
                                c.ConstantColumn(55);
                                c.RelativeColumn(1.2f);
                                c.ConstantColumn(60);
                                c.RelativeColumn(1.5f);
                                c.ConstantColumn(55);
                                c.RelativeColumn(1.1f);
                                c.RelativeColumn(1.1f);
                                c.RelativeColumn(1.1f);
                                c.RelativeColumn(1.1f);
                                c.ConstantColumn(85);
                                c.ConstantColumn(95);
                            });

                            Header(table, "STT");
                            Header(table, "Sub ID");
                            Header(table, "Loại SP");
                            Header(table, "Size");
                            Header(table, "Path BTP");
                            Header(table, "SL");
                            Header(table, "Giấy");
                            Header(table, "Keo phủ");
                            Header(table, "Màng");
                            Header(table, "Sóng");
                            Header(table, "Giá/SP");
                            Header(table, "Tổng giá");

                            var index = 1;

                            foreach (var item in items)
                            {
                                Cell(table, index.ToString());
                                Cell(table, item.id.ToString());
                                Cell(table, item.product_type?.name ?? item.product_type_id.ToString());
                                Cell(table, $"{item.width ?? 0}x{item.length ?? 0}");
                                Cell(table, item.product_process ?? "");
                                Cell(table, Format(item.quantity));
                                Cell(table, item.paper_material_code ?? "");
                                Cell(table, item.coating_material_code ?? "");
                                Cell(table, item.lamination_material_code ?? "");
                                Cell(table, item.wave_material_code ?? "");
                                Cell(table, Format(item.unit_cost_to_stage));
                                Cell(table, Format(item.total_cost_to_stage));

                                index++;
                            }
                        });

                        var totalQty = items.Sum(x => x.quantity);
                        var totalValue = items.Sum(x => x.total_cost_to_stage);

                        col.Item().AlignRight().Text(
                            $"Tổng số lượng: {Format(totalQty)} | Tổng giá trị BTP: {Format(totalValue)} VND")
                            .Bold();

                        col.Item().PaddingTop(8).Text(
                            "Ghi chú: Các dòng chỉ được gộp tồn khi trùng loại sản phẩm, kích thước, path, NVL signature và đơn giá/SP.")
                            .Italic()
                            .FontSize(8);
                    });
                });
            }).GeneratePdf();
        }

        private static void Header(TableDescriptor table, string text)
        {
            table.Cell()
                .Border(0.5f)
                .Background(Colors.Grey.Lighten2)
                .Padding(4)
                .Text(text)
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

        private static string Format(decimal value)
            => string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);

        private static string Format(int value)
            => string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);
    }
}