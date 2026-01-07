using AMMS.Infrastructure.Entities;
using System.Globalization;
using System.Text;

namespace AMMS.Application.Helpers
{
    public static class DealEmailTemplates
    {
        private static string VND(decimal v)
            => string.Format(new CultureInfo("vi-VN"), "{0:N0} ₫", v);

        public static string QuoteEmail(order_request req, cost_estimate est, decimal deposit,
    string acceptUrl, string rejectUrl)
        {
            var address = $"{req.detail_address}";
            var delivery = req.delivery_date?.ToString("dd/MM/yyyy") ?? "N/A";

            return $@"
<html>
  <body style='font-family: Arial, Helvetica, sans-serif;'>
    <h2 style='margin-top:0;'>Báo giá đơn hàng in ấn</h2>

    <h3>Thông tin người đặt</h3>
    <table style='border-collapse:collapse;width:100%;margin-bottom:12px;'>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Tên</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{req.customer_name}</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>SĐT</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{req.customer_phone}</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>Email</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{req.customer_email}</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>Địa chỉ</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{address}</td>
      </tr>
    </table>

    <h3>Thông tin giao hàng</h3>
    <table style='border-collapse:collapse;width:100%;margin-bottom:12px;'>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Địa chỉ giao</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{address}</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>Ngày giao dự kiến</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{delivery}</td>
      </tr>
    </table>

    <h3>Thông tin đơn hàng</h3>
    <table style='border-collapse:collapse;width:100%;margin-bottom:16px;'>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Sản phẩm</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{req.product_name}</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>Số lượng</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{req.quantity}</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>Tổng giá trị đơn hàng</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{est.final_total_cost:n0} VND</td>
      </tr>
      <tr>
        <td style='border:1px solid transparent;padding:4px 8px;'><b>Số tiền đặt cọc</b></td>
        <td style='border:1px solid transparent;padding:4px 8px;'>{deposit:n0} VND</td>
      </tr>
    </table>

    <p style='margin:16px 0;'>
      <a href='{acceptUrl}'
         style='padding:10px 16px;background:#16a34a;color:white;text-decoration:none;border-radius:6px;display:inline-block;'>
        Đồng ý &amp; Thanh toán cọc
      </a>
      &nbsp;
      <a href='{rejectUrl}'
         style='padding:10px 16px;background:#dc2626;color:white;text-decoration:none;border-radius:6px;display:inline-block;'>
        Từ chối báo giá
      </a>
    </p>

    <p style='color:#666;font-size:12px;margin-top:16px;'>
      Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email.
    </p>
  </body>
</html>";
        }

        public static string QuoteEmailNeedDesign(
            order_request req,
            cost_estimate est,
            decimal deposit,
            string acceptUrl,
            string rejectUrl,
            string orderDetailUrl)
        {
            var delivery = req.delivery_date?.ToString("dd/MM/yyyy") ?? "N/A";
            var finalTotal = est.final_total_cost;

            return $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:720px;margin:24px auto;color:#111;line-height:1.6'>
  <h2 style='margin-top:0'>BÁO GIÁ ĐƠN HÀNG IN ẤN</h2>

  <p>Chào {req.customer_name},</p>
  <p>Chúng tôi gửi đến bạn báo giá cho đơn hàng <b>{req.product_name}</b> với các thông tin như sau:</p>

  <h3>Thông tin đơn hàng</h3>
  <table style='border-collapse:collapse;width:100%;margin-bottom:12px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Mã yêu cầu</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>AM{req.order_request_id:D6}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Sản phẩm</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.product_name}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Số lượng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.quantity}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Ngày giao dự kiến</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{delivery}</td>
    </tr>
  </table>

  <h3>Thông tin báo giá</h3>
  <table style='border-collapse:collapse;width:100%;margin-bottom:16px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Tổng giá trị</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{finalTotal:n0} VND</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Tiền cọc (dự kiến thu)</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{deposit:n0} VND</td>
    </tr>
  </table>

  <p style='margin:16px 0'>
    Vì bạn chọn phương án <b>Tự gửi file thiết kế</b>, vui lòng nhấn vào liên kết dưới đây để
    xem chi tiết đơn hàng và tải lên/cập nhật file thiết kế:
  </p>

  <p style='text-align:center;margin:18px 0'>
    <a href='{orderDetailUrl}'
       style='display:inline-block;padding:10px 18px;border-radius:6px;
              background:#2563eb;color:#ffffff;text-decoration:none;font-weight:600'>
      Xem chi tiết đơn hàng &amp; gửi thiết kế
    </a>
  </p>

  <p style='margin:18px 0'>
    Sau khi xem chi tiết và thống nhất, bạn có thể:
  </p>

  <table style='border-collapse:collapse;width:100%;margin-bottom:8px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;vertical-align:top;'><b>Đồng ý báo giá</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>
        <a href='{acceptUrl}' style='color:#16a34a;font-weight:600;text-decoration:none;'>
          Nhấn vào đây để đồng ý &amp; thanh toán tiền cọc
        </a>
      </td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;vertical-align:top;'><b>Từ chối báo giá</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>
        <a href='{rejectUrl}' style='color:#dc2626;text-decoration:none;'>
          Nhấn vào đây để từ chối báo giá
        </a>
      </td>
    </tr>
  </table>

  <p>Nếu bạn có bất kỳ thắc mắc nào, hãy phản hồi lại email này để được hỗ trợ.</p>

  <p>Trân trọng,<br/>Đội ngũ AMMS</p>
</div>";
        }

        public static string AcceptCustomerEmail(
            order_request req,
            order order,
            cost_estimate est,
            string trackingUrl)
        {
            return $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:720px;margin:24px auto;line-height:1.6'>
  <h2 style='margin-top:0;'>ĐƠN HÀNG ĐÃ ĐƯỢC PHÊ DUYỆT</h2>

  <p>Cảm ơn bạn đã xác nhận báo giá.</p>

  <table style='border-collapse:collapse;width:100%;margin:12px 0 16px 0;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Mã đơn hàng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'><span style='color:blue'>{order.code}</span></td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Sản phẩm</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.product_name}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Số lượng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.quantity}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Giá trị đơn hàng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{VND(est.final_total_cost)}</td>
    </tr>
  </table>

  <hr style='border:none;border-top:1px solid #e5e7eb;margin:16px 0;' />

  <p>
    Bạn có thể theo dõi tiến độ sản xuất tại:
    <br/>
    <a href='{trackingUrl}'>{trackingUrl}</a>
  </p>

  <p>
    Vui lòng lưu lại <b>mã đơn hàng</b> để tra cứu sau này.
  </p>

  <p>AMMS trân trọng!</p>
</div>";
        }

        public static string AcceptConsultantEmail(
            order_request req,
            order order)
        {
            return $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:720px;margin:24px auto;line-height:1.6'>
  <h3 style='margin-top:0;'>KHÁCH HÀNG ĐÃ ĐỒNG Ý BÁO GIÁ</h3>

  <table style='border-collapse:collapse;width:100%;margin-top:8px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Request ID</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.order_request_id}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Order Code</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>{order.code}</b></td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Sản phẩm</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.product_name}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Số lượng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{req.quantity}</td>
    </tr>
  </table>
</div>";
        }
    }
}
