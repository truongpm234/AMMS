using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace AMMS.Application.Helpers
{
    public sealed class ProductionIssueReceiptPdfModel
    {
        public string receipt_no { get; set; } = "";
        public string title { get; set; } = "";
        public DateTime created_at { get; set; }

        public int prod_id { get; set; }
        public string? production_code { get; set; }
        public string? prod_kind { get; set; }
        public string? prod_method { get; set; }

        public List<ProductionIssueReceiptPdfLineModel> lines { get; set; } = new();
    }

    public sealed class ProductionIssueReceiptPdfLineModel
    {
        public int? order_id { get; set; }

        public int prod_id { get; set; }

        public string item_type { get; set; } = "";

        public int? material_id { get; set; }

        public int? sub_product_id { get; set; }

        public string code { get; set; } = "";

        public string name { get; set; } = "";

        public decimal qty { get; set; }

        public string unit { get; set; } = "";

        public string note { get; set; } = "";
    }

    public static class ProductionIssueReceiptPdfHelper
    {
        public static byte[] GeneratePdf(ProductionIssueReceiptPdfModel model)
        {
            model.lines ??= new List<ProductionIssueReceiptPdfLineModel>();

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

                        col.Item().AlignCenter()
                            .Text(string.IsNullOrWhiteSpace(model.title)
                                ? "PHIẾU XUẤT KHO SẢN XUẤT"
                                : model.title)
                            .Bold()
                            .FontSize(18);

                        col.Item().AlignCenter()
                            .Text($"Số phiếu: {model.receipt_no} - Ngày tạo: {model.created_at:dd/MM/yyyy HH:mm}");

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(110);
                                c.RelativeColumn();
                                c.ConstantColumn(110);
                                c.RelativeColumn();
                            });

                            InfoCell(table, "Production");
                            InfoCell(table, $"{model.prod_id} - {model.production_code}");

                            InfoCell(table, "Loại production");
                            InfoCell(table, model.prod_kind ?? "");

                            InfoCell(table, "Method");
                            InfoCell(table, model.prod_method ?? "");

                            InfoCell(table, "Tổng dòng xuất");
                            InfoCell(table, model.lines.Count.ToString(CultureInfo.InvariantCulture));
                        });

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(32);
                                c.ConstantColumn(58);
                                c.ConstantColumn(58);
                                c.ConstantColumn(82);
                                c.ConstantColumn(70);
                                c.ConstantColumn(70);
                                c.RelativeColumn(1.4f);
                                c.ConstantColumn(70);
                                c.ConstantColumn(52);
                                c.RelativeColumn(1.2f);
                            });

                            Header(table, "STT");
                            Header(table, "Order");
                            Header(table, "Prod");
                            Header(table, "Loại");
                            Header(table, "MAT ID");
                            Header(table, "SUB ID");
                            Header(table, "Mã / Tên");
                            Header(table, "SL xuất");
                            Header(table, "ĐVT");
                            Header(table, "Ghi chú");

                            var index = 1;

                            foreach (var line in model.lines)
                            {
                                Cell(table, index.ToString(CultureInfo.InvariantCulture));
                                Cell(table, line.order_id?.ToString() ?? "");
                                Cell(table, line.prod_id.ToString(CultureInfo.InvariantCulture));
                                Cell(table, ResolveItemTypeName(line.item_type));
                                Cell(table, line.material_id?.ToString() ?? "");
                                Cell(table, line.sub_product_id?.ToString() ?? "");
                                Cell(table, $"{line.code}\n{line.name}");
                                Cell(table, FormatQty(line.qty));
                                Cell(table, line.unit ?? "");
                                Cell(table, line.note ?? "");

                                index++;
                            }
                        });

                        var totalMaterialQty = model.lines
                            .Where(x => string.Equals(x.item_type, "MATERIAL", StringComparison.OrdinalIgnoreCase))
                            .Sum(x => x.qty);

                        var totalSubQty = model.lines
                            .Where(x => string.Equals(x.item_type, "SUB_PRODUCT", StringComparison.OrdinalIgnoreCase))
                            .Sum(x => x.qty);

                        col.Item().AlignRight()
                            .Text($"Tổng NVL/vật tư: {FormatQty(totalMaterialQty)} | Tổng BTP: {FormatQty(totalSubQty)}")
                            .Bold();

                        col.Item().PaddingTop(8)
                            .Text("Phiếu được tạo tự động khi xác nhận lập lịch sản xuất.")
                            .Italic()
                            .FontSize(8);
                    });
                });
            }).GeneratePdf();
        }

        private static string ResolveItemTypeName(string? itemType)
        {
            var type = (itemType ?? "").Trim().ToUpperInvariant();

            return type switch
            {
                "MATERIAL" => "NVL/Vật tư",
                "SUB_PRODUCT" => "BTP",
                "NO_ISSUE" => "Không xuất kho",
                _ => string.IsNullOrWhiteSpace(type) ? "" : type
            };
        }

        private static void InfoCell(TableDescriptor table, string text)
        {
            table.Cell()
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Padding(5)
                .Text(text ?? "");
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

        private static string FormatQty(decimal value)
            => string.Format(CultureInfo.InvariantCulture, "{0:N4}", value).TrimEnd('0').TrimEnd('.');
    }
}