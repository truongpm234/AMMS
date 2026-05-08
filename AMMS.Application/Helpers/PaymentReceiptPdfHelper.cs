using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AMMS.Application.Helpers
{
    public static class PaymentReceiptPdfHelper
    {
        public static byte[] GeneratePdf(IDictionary<string, string> placeholders)
        {
            var p = new ReceiptPlaceholderReader(placeholders);

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

                    page.Header().Element(c => ComposeHeader(c, p));

                    page.Content().Element(c => ComposeContent(c, p));

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

        private static void ComposeHeader(IContainer container, ReceiptPlaceholderReader p)
        {
            container.Column(column =>
            {
                column.Spacing(4);

                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        left.Item().Text(p.Get("COMPANY_NAME"))
                            .Bold()
                            .FontSize(11);

                        left.Item().Text($"Địa chỉ: {p.Get("COMPANY_ADDRESS")}");
                        left.Item().Text($"Điện thoại: {p.Get("COMPANY_PHONE")}");
                        left.Item().Text($"Email: {p.Get("COMPANY_EMAIL")}");
                        left.Item().Text($"Mã số thuế: {p.Get("COMPANY_TAX_CODE")}");
                    });

                    row.ConstantItem(180).AlignRight().Column(right =>
                    {
                        right.Item().AlignCenter().Text("Mẫu phiếu thu")
                            .SemiBold();

                        right.Item().AlignCenter().Text($"Số: {p.Get("RECEIPT_NO")}")
                            .Bold();

                        right.Item().AlignCenter().Text($"PayOS: {p.Get("PAYOS_ORDER_CODE")}");
                    });
                });

                column.Item().PaddingTop(8).LineHorizontal(1);
            });
        }

        private static void ComposeContent(IContainer container, ReceiptPlaceholderReader p)
        {
            container.Column(column =>
            {
                column.Spacing(10);

                column.Item().PaddingTop(10).AlignCenter().Text("PHIẾU THU")
                    .Bold()
                    .FontSize(20);

                column.Item().AlignCenter().Text(
                    $"Ngày {p.Get("RECEIPT_DAY")} tháng {p.Get("RECEIPT_MONTH")} năm {p.Get("RECEIPT_YEAR")}");

                column.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(145);
                        columns.RelativeColumn();
                    });

                    AddRow("Người nộp tiền", p.Get("PAYER_NAME"));
                    AddRow("Địa chỉ", p.Get("PAYER_ADDRESS"));
                    AddRow("Nội dung thu", p.Get("RECEIPT_REASON"));
                    AddRow("Mã yêu cầu", p.Get("REQUEST_CODE"));
                    AddRow("Mã đơn hàng", p.Get("ORDER_CODE"));
                    AddRow("Mã báo giá", p.Get("QUOTE_ID"));
                    AddRow("Mã dự toán", p.Get("ESTIMATE_ID"));
                    AddRow("Sản phẩm", p.Get("PRODUCT_NAME"));
                    AddRow("Số lượng", p.Get("QUANTITY"));
                    AddRow("Loại thanh toán", p.Get("PAYMENT_TYPE_DISPLAY"));
                    AddRow("Phương thức thanh toán", p.Get("PAYMENT_METHOD"));
                    AddRow("Mã giao dịch", p.Get("TRANSACTION_ID"));

                    void AddRow(string label, string value)
                    {
                        table.Cell().Element(LabelCell).Text(label).SemiBold();
                        table.Cell().Element(ValueCell).Text(value);
                    }
                });

                column.Item().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.ConstantColumn(150);
                    });

                    AddMoneyRow("Tổng giá trị đơn hàng", p.Get("TOTAL_ORDER_VALUE"));
                    AddMoneyRow("Đã thanh toán trước phiếu này", p.Get("PAID_BEFORE"));
                    AddMoneyRow("Số tiền thu lần này", p.Get("PAYMENT_AMOUNT"), bold: true);
                    AddMoneyRow("Còn lại sau phiếu này", p.Get("REMAINING_AFTER"));

                    void AddMoneyRow(string label, string value, bool bold = false)
                    {
                        var labelText = table.Cell().Element(LabelCell).Text(label);
                        var valueText = table.Cell().Element(ValueCell).AlignRight().Text($"{value} VND");

                        if (bold)
                        {
                            labelText.Bold();
                            valueText.Bold();
                        }
                    }
                });

                column.Item().PaddingTop(4).Text(text =>
                {
                    text.Span("Số tiền bằng chữ: ").SemiBold();
                    text.Span($"{p.Get("PAYMENT_AMOUNT_TEXT")} đồng");
                });

                column.Item().PaddingTop(20).Row(row =>
                {
                    row.RelativeItem().AlignCenter().Column(col =>
                    {
                        col.Item().Text("Người nộp tiền").Bold();
                        col.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                        col.Item().PaddingTop(60).Text(p.Get("PAYER_SIGN_NAME")).SemiBold();
                    });

                    row.RelativeItem().AlignCenter().Column(col =>
                    {
                        col.Item().Text("Người lập phiếu").Bold();
                        col.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                        col.Item().PaddingTop(60).Text(p.Get("CONSULTANT_SIGN_NAME")).SemiBold();
                    });

                    row.RelativeItem().AlignCenter().Column(col =>
                    {
                        col.Item().Text("Kế toán").Bold();
                        col.Item().PaddingTop(4).Text("(Ký, ghi rõ họ tên)").Italic();
                        col.Item().PaddingTop(60).Text("");
                    });
                });

                column.Item().PaddingTop(16).Text(
                    "Phiếu thu được tạo tự động từ hệ thống AMMS sau khi giao dịch thanh toán được ghi nhận thành công.")
                    .FontSize(8)
                    .Italic();
            });
        }

        private static IContainer LabelCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Background(Colors.Grey.Lighten3)
                .PaddingVertical(5)
                .PaddingHorizontal(6);
        }

        private static IContainer ValueCell(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .PaddingVertical(5)
                .PaddingHorizontal(6);
        }

        private sealed class ReceiptPlaceholderReader
        {
            private readonly IDictionary<string, string> _values;

            public ReceiptPlaceholderReader(IDictionary<string, string> values)
            {
                _values = values;
            }

            public string Get(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                    return "";

                var wrappedKey = "{{" + key.Trim() + "}}";

                if (_values.TryGetValue(wrappedKey, out var wrappedValue))
                    return wrappedValue ?? "";

                if (_values.TryGetValue(key.Trim(), out var rawValue))
                    return rawValue ?? "";

                return "";
            }
        }
    }
}
