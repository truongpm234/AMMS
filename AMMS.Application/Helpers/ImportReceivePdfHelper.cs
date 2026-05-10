using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AMMS.Application.Helpers
{
    public static class ImportReceivePdfHelper
    {
        public static void Generate(string filePath, ImportReceiveSourceDto source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(30);

                    page.Header()
                        .AlignCenter()
                        .Text("PHIẾU NHẬP KHO THÀNH PHẨM")
                        .Bold()
                        .FontSize(20);

                    page.Content()
                        .PaddingVertical(20)
                        .Column(col =>
                        {
                            col.Spacing(10);

                            col.Item().Text($"Ngày tạo phiếu: {AppTime.NowVnUnspecified():dd/MM/yyyy HH:mm}");
                            col.Item().Text($"Mã đơn: {source.order_code}");
                            col.Item().Text($"ID Production: {source.prod_id}");
                            col.Item().Text($"ID Order: {source.order_id}");

                            col.Item().PaddingTop(15);

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.ConstantColumn(40);   // STT
                                    columns.ConstantColumn(60);   // ID
                                    columns.RelativeColumn(1);    // Mã đơn
                                    columns.RelativeColumn(2);    // Tên thành phẩm
                                    columns.RelativeColumn(2);    // Quy cách
                                    columns.RelativeColumn(1);    // Số lượng
                                });

                                // Header
                                table.Header(header =>
                                {
                                    header.Cell().Element(CellStyle).Text("STT").Bold();
                                    header.Cell().Element(CellStyle).Text("ID").Bold();
                                    header.Cell().Element(CellStyle).Text("Mã đơn").Bold();
                                    header.Cell().Element(CellStyle).Text("Tên thành phẩm").Bold();
                                    header.Cell().Element(CellStyle).Text("Quy cách đóng gói").Bold();
                                    header.Cell().Element(CellStyle).Text("Tổng Số lượng sản phẩm").Bold();
                                });

                                int stt = 1;

                                foreach (var item in source.items)
                                {
                                    table.Cell().Element(CellStyle).Text(stt.ToString());
                                    table.Cell().Element(CellStyle).Text(item.item_id.ToString());
                                    table.Cell().Element(CellStyle).Text(source.order_code);
                                    table.Cell().Element(CellStyle).Text(item.product_name);

                                    table.Cell().Element(CellStyle).Text(
                                        string.IsNullOrWhiteSpace(item.packaging_standard)
                                            ? "Chưa có dữ liệu"
                                            : item.packaging_standard
                                    );

                                    table.Cell().Element(CellStyle).Text(item.quantity.ToString());

                                    stt++;
                                }
                            });

                            col.Item()
                                .PaddingTop(20)
                                .Text("Phiếu được tạo tự động từ hệ thống.")
                                .Italic();
                        });

                    page.Footer()
                        .AlignRight()
                        .Text(x =>
                        {
                            x.Span("Generated at ");
                            x.Span(DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
                        });
                });
            })
            .GeneratePdf(filePath);
        }

        private static IContainer CellStyle(IContainer container)
        {
            return container
                .Border(1)
                .Padding(5)
                .AlignMiddle();
        }
    }
}