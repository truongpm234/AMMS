using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Net;
using AMMS.Infrastructure.Entities;

namespace AMMS.Application.Helpers
{
    public static class RemainingPaymentEmailTemplates
    {
        private const string FontFamily = "\"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, sans-serif";

        private static string VND(decimal v)
            => string.Format(new CultureInfo("vi-VN"), "{0:N0} ₫", v);

        private static string Safe(string? s)
            => WebUtility.HtmlEncode((s ?? "").Trim());

        private static string PlainPaymentUrlBlock(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "";

            var encodedUrl = WebUtility.HtmlEncode(url);

            var safeUrl = encodedUrl
                .Replace("://", "<span>://</span>")
                .Replace(".", "<span>.</span>");

            return $@"
<div style='margin:0 0 24px 0;max-width:100%;background:#f0f9ff;border:2px solid #bae6fd;border-radius:12px;padding:24px 20px;text-align:left;box-shadow:0 4px 15px rgba(0,0,0,0.04);'>
  <p style='font-size:22px;color:#0369a1;font-weight:900;margin:0 0 12px 0;line-height:1.4;'>
    📌 Đường dẫn thanh toán
  </p>

  <p style='font-size:15px;color:#334155;line-height:1.6;margin:0 0 16px 0;'>
    Xin cảm ơn quý khách hàng đã đồng hành cùng chúng tôi. Vui lòng <b>copy đường dẫn bên dưới</b> và dán vào tab mới của trình duyệt để tiếp tục thanh toán phần còn lại của đơn hàng.
  </p>

  <div style='font-size:20px;color:#0284c7;word-break:break-all;line-height:1.6;margin:0;font-family:""Segoe UI"", Roboto, ""Helvetica Neue"", Arial, sans-serif;font-weight:900;background:#ffffff;border:2px dashed #7dd3fc;border-radius:8px;padding:16px 20px;user-select:all;-webkit-user-select:all;text-decoration:none;cursor:text;text-align:center;'>
    {safeUrl}
  </div>
</div>";
        }

        public static string BuildOrderFinishedRemainingPaymentEmail(
            order_request req,
            order ord,
            production? prod,
            cost_estimate est,
            decimal remainingAmount,
            string paymentPageUrl)
        {
            var total = est.final_total_cost;
            var deposit = est.deposit_amount;
            var productName = req.product_name ?? "";
            var quantity = req.quantity ?? 0;

            return $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1'>
</head>
<body style='margin:0;background:#f8fafc;padding:30px 0;font-family:{FontFamily};'>
  <div style='max-width:760px;margin:0 auto;padding:0 12px;'>

    <div style='background:linear-gradient(135deg,#1d4ed8 0%,#1e3a8a 100%);padding:24px 26px;border-radius:18px 18px 0 0;color:#ffffff;'>
      <div style='font-size:11px;font-weight:800;letter-spacing:1px;text-transform:uppercase;opacity:0.9;'>MES PAYMENT NOTICE</div>
      <div style='font-size:24px;font-weight:900;margin-top:8px;'>Đơn hàng đã hoàn thành</div>
      <div style='font-size:13px;margin-top:6px;color:#dbeafe;'>
        Vui lòng thanh toán phần còn lại để chúng tôi chuyển đơn sang bộ phận vận chuyển.
      </div>
    </div>

    <div style='background:#ffffff;border:1px solid #e2e8f0;border-top:none;border-radius:0 0 18px 18px;padding:24px 24px 20px;box-shadow:0 10px 28px rgba(15,23,42,0.06);'>

      <p style='margin:0 0 14px 0;font-size:14px;color:#334155;line-height:1.8;'>
        Kính gửi <b>{Safe(req.customer_name)}</b>,
      </p>

      <p style='margin:0 0 14px 0;font-size:14px;color:#334155;line-height:1.8;'>
        Chúng tôi chân thành cảm ơn Quý khách đã tin tưởng sử dụng dịch vụ của doanh nghiệp.
        Đơn hàng của Quý khách hiện đã hoàn thành toàn bộ công đoạn sản xuất.
      </p>

      <p style='margin:0 0 16px 0;font-size:14px;color:#334155;line-height:1.8;'>
        Để chúng tôi tiếp tục chuyển đơn hàng sang bộ phận vận chuyển và tiến hành giao hàng,
        Quý khách vui lòng thanh toán <b>phần giá trị còn lại</b> của đơn hàng theo thông tin bên dưới.
      </p>

      <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:14px;padding:16px 18px;margin:14px 0 18px 0;'>
        <div style='font-size:13px;font-weight:800;color:#334155;margin-bottom:10px;text-transform:uppercase;'>Thông tin đơn hàng</div>

        <table width='100%' cellpadding='0' cellspacing='0' style='border-collapse:collapse;'>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#64748b;width:38%;'>Mã đơn hàng</td>
            <td style='padding:6px 0;font-size:13px;color:#0f172a;font-weight:700;'>#{Safe(ord.code)}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#64748b;'>Mã request</td>
            <td style='padding:6px 0;font-size:13px;color:#0f172a;font-weight:700;'>AM{req.order_request_id:D6}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#64748b;'>Sản phẩm</td>
            <td style='padding:6px 0;font-size:13px;color:#0f172a;font-weight:700;'>{Safe(productName)}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#64748b;'>Số lượng</td>
            <td style='padding:6px 0;font-size:13px;color:#0f172a;font-weight:700;'>{quantity:N0}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#64748b;'>Ngày giao dự kiến</td>
            <td style='padding:6px 0;font-size:13px;color:#0f172a;font-weight:700;'>{req.delivery_date:dd/MM/yyyy}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#64748b;'>Trạng thái sản xuất</td>
            <td style='padding:6px 0;font-size:13px;color:#0f172a;font-weight:700;'>Đã hoàn thành</td>
          </tr>
        </table>
      </div>

      <div style='background:linear-gradient(135deg,#fff7ed 0%,#fffbeb 100%);border:1px solid #fed7aa;border-radius:14px;padding:16px 18px;margin:0 0 18px 0;'>
        <div style='font-size:13px;font-weight:800;color:#9a3412;margin-bottom:10px;text-transform:uppercase;'>Thông tin thanh toán</div>

        <table width='100%' cellpadding='0' cellspacing='0' style='border-collapse:collapse;'>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#7c2d12;width:45%;'>Tổng giá trị đơn hàng</td>
            <td style='padding:6px 0;font-size:13px;color:#7c2d12;font-weight:700;text-align:right;'>{VND(total)}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;font-size:13px;color:#7c2d12;'>Đã thanh toán tiền cọc</td>
            <td style='padding:6px 0;font-size:13px;color:#7c2d12;font-weight:700;text-align:right;'>{VND(deposit)}</td>
          </tr>
          <tr>
            <td style='padding:10px 0 6px 0;font-size:14px;color:#9a3412;font-weight:900;border-top:1px dashed #fdba74;'>Số tiền cần thanh toán còn lại</td>
            <td style='padding:10px 0 6px 0;font-size:18px;color:#b45309;font-weight:900;text-align:right;border-top:1px dashed #fdba74;'>{VND(remainingAmount)}</td>
          </tr>
        </table>
      </div>

      {PlainPaymentUrlBlock(paymentPageUrl)}

      <div style='margin-top:12px;background:#f8fafc;border:1px solid #cbd5e1;border-radius:12px;padding:14px 16px;'>
        <p style='margin:0 0 8px 0;font-size:13px;color:#334155;line-height:1.7;'>
          Sau khi hệ thống xác nhận thanh toán thành công, đơn hàng sẽ được chuyển sang bước giao hàng để gửi cho đơn vị vận chuyển.
        </p>
        <p style='margin:0;font-size:12px;color:#64748b;line-height:1.7;'>
          Nếu Quý khách cần hỗ trợ thêm về đơn hàng hoặc thanh toán, vui lòng phản hồi lại email này hoặc liên hệ bộ phận chăm sóc khách hàng của chúng tôi.
        </p>
      </div>

      <p style='margin:18px 0 0 0;font-size:13px;color:#475569;line-height:1.7;'>
        Xin chân thành cảm ơn Quý khách đã đồng hành cùng doanh nghiệp.
      </p>
    </div>

    <div style='padding:14px;text-align:center;font-size:12px;color:#64748b;'>
      Email này được gửi tự động từ hệ thống MES.
    </div>
  </div>
</body>
</html>";
        }
    }
}
