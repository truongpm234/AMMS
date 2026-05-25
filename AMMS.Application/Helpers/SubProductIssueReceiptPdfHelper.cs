using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;

namespace AMMS.Application.Helpers
{
    public sealed class SubProductIssueReceiptPdfModel
    {
        public string receipt_no { get; set; } = "";
        public DateTime created_at { get; set; }

        public int prod_id { get; set; }
        public string? production_code { get; set; }

        public int order_id { get; set; }
        public string? order_code { get; set; }
        public string? customer_name { get; set; }

        public int sub_product_id { get; set; }
        public string? product_type_name { get; set; }

        public int? width { get; set; }
        public int? length { get; set; }

        public string? product_process { get; set; }

        public string? paper_material_code { get; set; }
        public string? coating_material_code { get; set; }
        public string? lamination_material_code { get; set; }
        public string? wave_material_code { get; set; }
        public string? material_signature { get; set; }

        public int quantity_issued { get; set; }
        public int quantity_after_issue { get; set; }

        public decimal unit_cost_to_stage { get; set; }
        public decimal total_cost_to_stage { get; set; }

        public string? reason { get; set; }
    }

    public static class SubProductIssueReceiptPdfHelper
    {
        public static byte[] GeneratePdf(SubProductIssueReceiptPdfModel model)
        {
            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(28);
                    page.PageColor(Colors.White);

                    page.DefaultTextStyle(x => x
                        .FontFamily("DejaVu Sans")
                        .FontSize(10));

                    page.Content().Column(col =>
                    {
                        col.Spacing(12);

                        col.Item().AlignCenter()
                            .Text("PHIẾU XUẤT KHO BÁN THÀNH PHẨM")
                            .Bold()
                            .FontSize(18);

                        col.Item().AlignCenter()
                            .Text($"Số phiếu: {model.receipt_no} - Ngày tạo: {model.created_at:dd/MM/yyyy HH:mm}");

                        col.Item().LineHorizontal(0.5f);

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(150);
                                c.RelativeColumn();
                            });

                            Row(table, "Production", $"{model.prod_id} - {model.production_code}");
                            Row(table, "Order", $"{model.order_id} - {model.order_code}");
                            Row(table, "Khách hàng", model.customer_name ?? "");
                            Row(table, "Sub product ID", model.sub_product_id.ToString());
                            Row(table, "Loại sản phẩm", model.product_type_name ?? "");
                            Row(table, "Kích thước", $"{model.width ?? 0} x {model.length ?? 0}");
                            Row(table, "Path BTP", model.product_process ?? "");
                            Row(table, "Giấy", model.paper_material_code ?? "");
                            Row(table, "Keo phủ", model.coating_material_code ?? "");
                            Row(table, "Màng cán", model.lamination_material_code ?? "");
                            Row(table, "Sóng", model.wave_material_code ?? "");
                            Row(table, "Material signature", model.material_signature ?? "");
                        });

                        col.Item().PaddingTop(8).Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn();
                            });

                            Header(table, "Số lượng xuất");
                            Header(table, "Tồn sau xuất");
                            Header(table, "Giá/SP tới stage");
                            Header(table, "Giá trị xuất");

                            Cell(table, Format(model.quantity_issued));
                            Cell(table, Format(model.quantity_after_issue));
                            Cell(table, FormatMoney(model.unit_cost_to_stage));
                            Cell(table, FormatMoney(model.total_cost_to_stage));
                        });

                        if (!string.IsNullOrWhiteSpace(model.reason))
                        {
                            col.Item()
                                .PaddingTop(8)
                                .Text($"Ghi chú: {model.reason}")
                                .Italic()
                                .FontSize(9);
                        }

                        col.Item().PaddingTop(12)
                            .Text("Phiếu được tạo tự động từ hệ thống khi phương thức sản xuất SUB được duyệt.")
                            .Italic()
                            .FontSize(9);
                    });
                });
            }).GeneratePdf();
        }

        private static void Row(TableDescriptor table, string label, string value)
        {
            table.Cell()
                .Border(0.5f)
                .Background(Colors.Grey.Lighten3)
                .Padding(5)
                .Text(label)
                .SemiBold();

            table.Cell()
                .Border(0.5f)
                .Padding(5)
                .Text(value ?? "");
        }

        private static void Header(TableDescriptor table, string text)
        {
            table.Cell()
                .Border(0.5f)
                .Background(Colors.Grey.Lighten2)
                .Padding(5)
                .Text(text)
                .SemiBold();
        }

        private static void Cell(TableDescriptor table, string text)
        {
            table.Cell()
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Padding(5)
                .Text(text ?? "");
        }

        private static string Format(int value)
            => string.Format(CultureInfo.InvariantCulture, "{0:N0}", value);

        private static string FormatMoney(decimal value)
            => string.Format(CultureInfo.InvariantCulture, "{0:N0} VND", value);
    }
}