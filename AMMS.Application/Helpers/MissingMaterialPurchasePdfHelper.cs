using AMMS.Shared.DTOs.Orders;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AMMS.Application.Helpers
{
    public static class MissingMaterialPurchasePdfHelper
    {
        public static void Generate(
            string outputPath,
            List<MissingMaterialPurchasePdfRowDto> rows)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            var now = DateTime.Now;
            var totalAmount = rows.Sum(x => x.total_price);

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(25);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("MATERIAL PURCHASE FILE")
                            .FontSize(18)
                            .Bold()
                            .AlignCenter();

                        col.Item().PaddingTop(4).Text("Phiếu mua nguyên vật liệu")
                            .FontSize(13)
                            .SemiBold()
                            .AlignCenter();

                        col.Item().PaddingTop(8).Row(row =>
                        {
                            row.RelativeItem().Text($"Created at: {now:dd/MM/yyyy HH:mm}");
                            row.RelativeItem().AlignRight().Text($"Total rows: {rows.Count}");
                        });

                        col.Item().PaddingTop(5).LineHorizontal(1);
                    });

                    page.Content().PaddingTop(12).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(40);   // No.
                            columns.ConstantColumn(65);   // miss_id
                            columns.ConstantColumn(75);   // material_id
                            columns.RelativeColumn(2);    // material_name
                            columns.ConstantColumn(85);   // quantity
                            columns.ConstantColumn(95);   // total_price
                        });

                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderCellStyle).Text("No.");
                            header.Cell().Element(HeaderCellStyle).Text("miss_id");
                            header.Cell().Element(HeaderCellStyle).Text("material_id");
                            header.Cell().Element(HeaderCellStyle).Text("material_name");
                            header.Cell().Element(HeaderCellStyle).Text("quantity");
                            header.Cell().Element(HeaderCellStyle).Text("total_price");
                        });

                        var index = 1;

                        foreach (var item in rows)
                        {
                            table.Cell().Element(BodyCellStyle).Text(index.ToString());
                            table.Cell().Element(BodyCellStyle).Text(item.miss_id.ToString());
                            table.Cell().Element(BodyCellStyle).Text(item.material_id.ToString());
                            table.Cell().Element(BodyCellStyle).Text(item.material_name ?? "");
                            table.Cell().Element(BodyCellStyle).AlignRight().Text(FormatQty(item.quantity));
                            table.Cell().Element(BodyCellStyle).AlignRight().Text(FormatMoney(item.total_price));

                            index++;
                        }
                    });

                    page.Footer().Column(col =>
                    {
                        col.Item().PaddingTop(12).LineHorizontal(1);

                        col.Item().PaddingTop(8).Row(row =>
                        {
                            row.RelativeItem().Text("Total amount:")
                                .FontSize(11)
                                .Bold();

                            row.RelativeItem().AlignRight().Text(FormatMoney(totalAmount))
                                .FontSize(11)
                                .Bold();
                        });

                        col.Item().PaddingTop(15).AlignCenter().Text(x =>
                        {
                            x.Span("Page ");
                            x.CurrentPageNumber();
                            x.Span(" / ");
                            x.TotalPages();
                        });
                    });
                });
            }).GeneratePdf(outputPath);
        }

        private static IContainer HeaderCellStyle(IContainer container)
        {
            return container
                .Background(Colors.Grey.Lighten2)
                .Border(1)
                .BorderColor(Colors.Grey.Medium)
                .Padding(5)
                .AlignCenter()
                .AlignMiddle();
        }

        private static IContainer BodyCellStyle(IContainer container)
        {
            return container
                .Border(1)
                .BorderColor(Colors.Grey.Lighten1)
                .Padding(5)
                .AlignMiddle();
        }

        private static string FormatQty(decimal value)
        {
            return value.ToString("#,##0.####");
        }

        private static string FormatMoney(decimal value)
        {
            return value.ToString("#,##0");
        }
    }
}