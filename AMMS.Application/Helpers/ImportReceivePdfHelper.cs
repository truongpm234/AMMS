using AMMS.Shared.DTOs.Productions;
using AMMS.Shared.Helpers;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AMMS.Application.Helpers
{
    public static class ImportReceivePdfHelper
    {
        // Giữ hàm cũ để không làm vỡ code cũ
        public static void Generate(string filePath, ImportReceiveSourceDto source)
        {
            Generate(filePath, new List<ImportReceiveSourceDto> { source });
        }

        // Hàm mới: 1 file chứa nhiều production
        public static void Generate(string filePath, List<ImportReceiveSourceDto> sources)
        {
            sources ??= new List<ImportReceiveSourceDto>();

            if (sources.Count == 0)
                throw new InvalidOperationException("Không có production để tạo phiếu nhập kho.");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var first = sources.First();

            var prodIds = sources
                .Select(x => x.prod_id)
                .Where(x => x > 0)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

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
                            col.Item().Text($"Mã đơn: {first.order_code}");
                            col.Item().Text($"ID Order: {first.order_id}");
                            col.Item().Text($"Danh sách Production ID: {string.Join(", ", prodIds)}");
                            col.Item().Text($"Tổng số production trong phiếu: {prodIds.Count}");

                            col.Item().PaddingTop(15);

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.ConstantColumn(35);   // STT
                                    columns.ConstantColumn(70);   // Prod ID
                                    columns.ConstantColumn(55);   // Item ID
                                    columns.RelativeColumn(1);    // Mã đơn
                                    columns.RelativeColumn(2);    // Tên thành phẩm
                                    columns.RelativeColumn(2);    // Quy cách
                                    columns.RelativeColumn(1);    // Số lượng
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Element(HeaderCellStyle).Text("STT").Bold();
                                    header.Cell().Element(HeaderCellStyle).Text("Prod ID").Bold();
                                    header.Cell().Element(HeaderCellStyle).Text("Item ID").Bold();
                                    header.Cell().Element(HeaderCellStyle).Text("Mã đơn").Bold();
                                    header.Cell().Element(HeaderCellStyle).Text("Tên thành phẩm").Bold();
                                    header.Cell().Element(HeaderCellStyle).Text("Quy cách đóng gói").Bold();
                                    header.Cell().Element(HeaderCellStyle).Text("Số lượng").Bold();
                                });

                                int stt = 1;

                                foreach (var source in sources.OrderBy(x => x.prod_id))
                                {
                                    foreach (var item in source.items ?? new List<ImportReceiveItemDto>())
                                    {
                                        table.Cell().Element(CellStyle).Text(stt.ToString());
                                        table.Cell().Element(CellStyle).Text(source.prod_id.ToString());
                                        table.Cell().Element(CellStyle).Text(item.item_id.ToString());
                                        table.Cell().Element(CellStyle).Text(source.order_code ?? "");
                                        table.Cell().Element(CellStyle).Text(item.product_name ?? "");

                                        table.Cell().Element(CellStyle).Text(
                                            string.IsNullOrWhiteSpace(item.packaging_standard)
                                                ? "Chưa có dữ liệu"
                                                : item.packaging_standard
                                        );

                                        table.Cell().Element(CellStyle).Text(item.quantity.ToString());

                                        stt++;
                                    }
                                }
                            });

                            col.Item()
                                .PaddingTop(20)
                                .Text("Phiếu được tạo tự động từ hệ thống. Một phiếu có thể bao gồm nhiều lệnh sản xuất của cùng một đơn hàng.")
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

        private static IContainer HeaderCellStyle(IContainer container)
        {
            return container
                .Border(1)
                .Background(Colors.Grey.Lighten3)
                .Padding(5)
                .AlignMiddle();
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