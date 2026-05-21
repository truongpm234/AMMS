using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Infrastructure.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AMMS.Application.Helpers
{
    public static class SubProductImportReceiptPdfHelper
    {
        public static byte[] GeneratePdf(sub_product sp)
        {
            var productTypeName = sp.product_type?.name ?? $"ProductType #{sp.product_type_id}";
            var now = DateTime.Now;

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(32);
                    page.PageColor(Colors.White);

                    page.DefaultTextStyle(x => x
                        .FontFamily("DejaVu Sans")
                        .FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Column(left =>
                            {
                                left.Item().Text("AMMS - PHIẾU NHẬP BÁN THÀNH PHẨM")
                                    .Bold()
                                    .FontSize(15);

                                left.Item().Text("Phiếu được tạo tự động từ hệ thống sau khi công đoạn sản xuất báo cáo dư bán thành phẩm.")
                                    .FontSize(9)
                                    .FontColor(Colors.Grey.Darken1);
                            });

                            row.ConstantItem(170).AlignRight().Column(right =>
                            {
                                right.Item().Text($"Mã phiếu: SP-IN-{sp.id:D6}")
                                    .Bold();

                                right.Item().Text($"Ngày tạo: {now:dd/MM/yyyy HH:mm}");
                            });
                        });

                        col.Item().PaddingTop(10).LineHorizontal(1);
                    });

                    page.Content().PaddingTop(18).Column(col =>
                    {
                        col.Spacing(12);

                        col.Item().Text("THÔNG TIN BÁN THÀNH PHẨM")
                            .Bold()
                            .FontSize(13);

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.ConstantColumn(170);
                                columns.RelativeColumn();
                            });

                            AddRow("Mã bán thành phẩm", sp.id.ToString());
                            AddRow("Loại sản phẩm", productTypeName);
                            AddRow("Product type ID", sp.product_type_id.ToString());
                            AddRow("Kích thước", $"{sp.width?.ToString() ?? "-"} x {sp.length?.ToString() ?? "-"}");
                            AddRow("Công đoạn bán thành phẩm", sp.product_process ?? "-");
                            AddRow("Số lượng nhập", sp.quantity.ToString("N0"));
                            AddRow("Trạng thái active", sp.is_active ? "Đã active" : "Chờ nhập kho");
                            AddRow("Trạng thái nhập kho", sp.is_imported ? "Đã nhập" : "Chưa nhập");
                            AddRow("Nguồn task", sp.source_task_id?.ToString() ?? "-");
                            AddRow("Nguồn production", sp.source_prod_id?.ToString() ?? "-");
                            AddRow("Nguồn order", sp.source_order_id?.ToString() ?? "-");
                            AddRow("Ghi chú", sp.description ?? "-");

                            void AddRow(string label, string value)
                            {
                                table.Cell().Element(LabelCell).Text(label).SemiBold();
                                table.Cell().Element(ValueCell).Text(value);
                            }
                        });

                        col.Item().PaddingTop(10).Text("XÁC NHẬN")
                            .Bold()
                            .FontSize(13);

                        col.Item().Row(row =>
                        {
                            row.RelativeItem().AlignCenter().Column(c =>
                            {
                                c.Item().Text("Người lập phiếu").Bold();
                                c.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                                c.Item().PaddingTop(70).Text("");
                            });

                            row.RelativeItem().AlignCenter().Column(c =>
                            {
                                c.Item().Text("Thủ kho").Bold();
                                c.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                                c.Item().PaddingTop(70).Text("");
                            });

                            row.RelativeItem().AlignCenter().Column(c =>
                            {
                                c.Item().Text("Quản lý sản xuất").Bold();
                                c.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                                c.Item().PaddingTop(70).Text("");
                            });
                        });
                    });

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

        private static IContainer LabelCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Background(Colors.Grey.Lighten3)
                .PaddingVertical(6)
                .PaddingHorizontal(8);
        }

        private static IContainer ValueCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .PaddingVertical(6)
                .PaddingHorizontal(8);
        }
    }
}