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
<div style='background:linear-gradient(135deg,#1d4ed8 0%,#1e3a8a 100%);padding:24px 26px;border-radius:18px 18px 0 0;color:#ffffff;'>
  <div style='font-size:11px;font-weight:800;letter-spacing:1px;text-transform:uppercase;opacity:0.9;'>MES PAYMENT NOTICE</div>
  <div style='font-size:24px;font-weight:900;margin-top:8px;'>Đơn hàng đã hoàn thành sản xuất</div>
  <div style='font-size:13px;margin-top:6px;color:#dbeafe;'>
    Vui lòng thanh toán phần còn lại trong vòng 07 ngày kể từ ngày nhận được email này.
  </div>
</div>

<div style='background:#ffffff;border:1px solid #e2e8f0;border-top:none;border-radius:0 0 18px 18px;padding:24px 24px 20px;box-shadow:0 10px 28px rgba(15,23,42,0.06);'>

  <p style='margin:0 0 14px 0;font-size:14px;color:#334155;line-height:1.8;'>
    Kính gửi <b>{Safe(req.customer_name)}</b>,
  </p>

  <p style='margin:0 0 14px 0;font-size:14px;color:#334155;line-height:1.8;'>
    Chúng tôi chân thành cảm ơn Quý khách đã tin tưởng sử dụng dịch vụ của doanh nghiệp.
    Đơn hàng của Quý khách hiện đã hoàn thành sản xuất và đang chờ thanh toán phần còn lại.
  </p>

  <p style='margin:0 0 16px 0;font-size:14px;color:#334155;line-height:1.8;'>
    Quý khách vui lòng thanh toán <b>phần giá trị còn lại trong vòng 07 ngày kể từ ngày nhận được email này</b>.
    Nếu quá thời hạn trên hệ thống vẫn chưa ghi nhận thanh toán, đơn hàng sẽ
    <b>chưa được bàn giao cho đơn vị vận chuyển</b>.
  </p>

  <div style='background:#fff7ed;border:1px solid #fed7aa;border-radius:14px;padding:14px 16px;margin:0 0 18px 0;'>
    <div style='font-size:13px;font-weight:800;color:#9a3412;margin-bottom:6px;'>Lưu ý thanh toán</div>
    <div style='font-size:13px;color:#9a3412;line-height:1.7;'>
      Để đảm bảo tiến độ bàn giao, Quý khách vui lòng hoàn tất thanh toán đúng hạn.
      Sau khi hệ thống xác nhận thanh toán thành công, đơn hàng sẽ được chuyển sang bước bàn giao vận chuyển.
    </div>
  </div>

  <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:14px;padding:16px 18px;margin:0 0 18px 0;'>
    <table style='width:100%;border-collapse:collapse;font-size:13px;color:#334155;'>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Mã đơn hàng</td>
        <td style='padding:7px 0;text-align:right;font-weight:800;color:#0f172a;'>{Safe(ord.code)}</td>
      </tr>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Sản phẩm</td>
        <td style='padding:7px 0;text-align:right;font-weight:700;color:#0f172a;'>{Safe(productName)}</td>
      </tr>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Số lượng</td>
        <td style='padding:7px 0;text-align:right;font-weight:700;color:#0f172a;'>{quantity:N0}</td>
      </tr>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Tổng giá trị</td>
        <td style='padding:7px 0;text-align:right;font-weight:700;color:#0f172a;'>{VND(total)}</td>
      </tr>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Đã đặt cọc</td>
        <td style='padding:7px 0;text-align:right;font-weight:700;color:#0f172a;'>{VND(deposit)}</td>
      </tr>
      <tr>
        <td style='padding:10px 0 0 0;color:#dc2626;font-weight:800;border-top:1px solid #e2e8f0;'>Cần thanh toán</td>
        <td style='padding:10px 0 0 0;text-align:right;font-weight:900;color:#dc2626;border-top:1px solid #e2e8f0;font-size:16px;'>{VND(remainingAmount)}</td>
      </tr>
    </table>
  </div>

  <div style='text-align:center;margin:22px 0 18px 0;'>
    <a href='{Safe(paymentPageUrl)}'
       style='display:inline-block;background:#2563eb;color:#ffffff;text-decoration:none;font-weight:800;font-size:14px;padding:13px 22px;border-radius:12px;'>
      Thanh toán phần còn lại
    </a>
  </div>

  {PlainPaymentUrlBlock(paymentPageUrl)}

  <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:14px;padding:14px 16px;margin-top:18px;'>
    <p style='margin:0 0 8px 0;font-size:12px;color:#475569;line-height:1.7;'>
      Sau khi thanh toán thành công, hệ thống sẽ tự động ghi nhận và chuyển đơn hàng sang bước bàn giao vận chuyển.
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
</div>";
        }

        public static string BuildRemainingPaymentReminderEmail(
    order_request req,
    order ord,
    production? prod,
    cost_estimate est,
    decimal remainingAmount,
    string paymentPageUrl)
        {
            var productName = req.product_name ?? "";
            var quantity = req.quantity ?? 0;

            return $@"
<div style='background:linear-gradient(135deg,#f97316 0%,#c2410c 100%);padding:24px 26px;border-radius:18px 18px 0 0;color:#ffffff;'>
  <div style='font-size:11px;font-weight:800;letter-spacing:1px;text-transform:uppercase;opacity:0.9;'>MES PAYMENT REMINDER</div>
  <div style='font-size:24px;font-weight:900;margin-top:8px;'>Nhắc thanh toán phần còn lại</div>
  <div style='font-size:13px;margin-top:6px;color:#ffedd5;'>
    Đơn hàng đang chờ thanh toán để được bàn giao cho đơn vị vận chuyển.
  </div>
</div>

<div style='background:#ffffff;border:1px solid #e2e8f0;border-top:none;border-radius:0 0 18px 18px;padding:24px 24px 20px;box-shadow:0 10px 28px rgba(15,23,42,0.06);'>

  <p style='margin:0 0 14px 0;font-size:14px;color:#334155;line-height:1.8;'>
    Kính gửi <b>{Safe(req.customer_name)}</b>,
  </p>

  <p style='margin:0 0 14px 0;font-size:14px;color:#334155;line-height:1.8;'>
    Hệ thống ghi nhận đơn hàng <b>{Safe(ord.code)}</b> vẫn đang ở trạng thái chờ thanh toán phần còn lại.
  </p>

  <p style='margin:0 0 16px 0;font-size:14px;color:#334155;line-height:1.8;'>
    Quý khách vui lòng hoàn tất thanh toán để đơn hàng được bàn giao cho đơn vị vận chuyển.
    Nếu chưa thanh toán, đơn hàng sẽ tiếp tục được giữ ở trạng thái chờ thanh toán.
  </p>

  <div style='background:#fff7ed;border:1px solid #fed7aa;border-radius:14px;padding:14px 16px;margin:0 0 18px 0;'>
    <div style='font-size:13px;font-weight:800;color:#9a3412;margin-bottom:6px;'>Thông báo</div>
    <div style='font-size:13px;color:#9a3412;line-height:1.7;'>
      Nhân viên tư vấn có thể liên hệ Quý khách để hỗ trợ hoàn tất thanh toán.
    </div>
  </div>

  <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:14px;padding:16px 18px;margin:0 0 18px 0;'>
    <table style='width:100%;border-collapse:collapse;font-size:13px;color:#334155;'>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Mã đơn hàng</td>
        <td style='padding:7px 0;text-align:right;font-weight:800;color:#0f172a;'>{Safe(ord.code)}</td>
      </tr>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Sản phẩm</td>
        <td style='padding:7px 0;text-align:right;font-weight:700;color:#0f172a;'>{Safe(productName)}</td>
      </tr>
      <tr>
        <td style='padding:7px 0;color:#64748b;'>Số lượng</td>
        <td style='padding:7px 0;text-align:right;font-weight:700;color:#0f172a;'>{quantity:N0}</td>
      </tr>
      <tr>
        <td style='padding:10px 0 0 0;color:#dc2626;font-weight:800;border-top:1px solid #e2e8f0;'>Cần thanh toán</td>
        <td style='padding:10px 0 0 0;text-align:right;font-weight:900;color:#dc2626;border-top:1px solid #e2e8f0;font-size:16px;'>{VND(remainingAmount)}</td>
      </tr>
    </table>
  </div>

  <div style='text-align:center;margin:22px 0 18px 0;'>
    <a href='{Safe(paymentPageUrl)}'
       style='display:inline-block;background:#f97316;color:#ffffff;text-decoration:none;font-weight:800;font-size:14px;padding:13px 22px;border-radius:12px;'>
      Thanh toán ngay
    </a>
  </div>

  {PlainPaymentUrlBlock(paymentPageUrl)}

  <p style='margin:18px 0 0 0;font-size:13px;color:#475569;line-height:1.7;'>
    Xin chân thành cảm ơn Quý khách.
  </p>
</div>

<div style='padding:14px;text-align:center;font-size:12px;color:#64748b;'>
  Email này được gửi tự động từ hệ thống MES.
</div>";
        }
    }
}
