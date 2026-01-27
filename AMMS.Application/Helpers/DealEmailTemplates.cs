using AMMS.Infrastructure.Entities;
using System.Globalization;
using System.Text;

namespace AMMS.Application.Helpers
{
    public static class DealEmailTemplates
    {
        private static string VND(decimal v)
            => string.Format(new CultureInfo("vi-VN"), "{0:N0} ₫", v);
        private static string MapProcessCode(string code) => code.Trim().ToUpperInvariant() switch
        {
            "IN" => "In",
            "RALO" => "Ra lô",
            "CAT" => "Cắt",
            "CAN_MANG" => "Cán",
            "BOI" => "Bồi",
            "PHU" => "Phủ",
            "DUT" => "Dứt",
            "DAN" => "Dán",
            "BE" => "Bế",
            _ => code
        };
        private static string BuildProductionProcessText(order_request req, cost_estimate est)
        {
            var codes = new List<string>();

            if (!string.IsNullOrWhiteSpace(req.production_processes))
            {
                codes = req.production_processes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            else if (est.process_costs is { Count: > 0 })
            {
                codes = est.process_costs
                    .Select(p => p.process_code)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct()
                    .ToList();
            }

            if (codes.Count == 0)
                return "Không có / Không áp dụng";

            return string.Join(", ", codes.Select(MapProcessCode));
        }

        /// <summary>
        /// Email báo giá: dùng 1 form duy nhất.
        /// </summary>
        public static string QuoteEmail(order_request req, cost_estimate est, string orderDetailUrl)
        {           
            var address = $"{req.detail_address}";
            var delivery = req.delivery_date?.ToString("dd/MM/yyyy") ?? "N/A";
            var requestDate = req.order_request_date?.ToString("dd/MM/yyyy HH:mm") ?? "N/A";
            var productName = req.product_name ?? "";
            var quantity = req.quantity ?? 0;
            var paperName = string.IsNullOrWhiteSpace(req.paper_name) ? "N/A" : req.paper_name;
            var designType = req.is_send_design == true ? "Tự gửi file thiết kế" : "Sử dụng bản thiết kế của doanh nghiệp";
            var materialCost = est.paper_cost + est.ink_cost;
            var laborCost = est.process_costs != null
                ? est.process_costs
                    .Where(p => p.estimate_id == est.estimate_id)
                    .Sum(p => p.total_cost)
                : 0m;
            var otherFees = est.design_cost + est.overhead_cost;                      
            var rushAmount = est.rush_amount;
            var subtotal = est.subtotal;                                              
            var finalTotal = est.final_total_cost;                                    
            var discountPercent = est.discount_percent;
            var discountAmount = est.discount_amount;
            var deposit = est.deposit_amount;                                         
            var productionProcessText = BuildProductionProcessText(req, est);
            var expiredAt = est.created_at.AddHours(24);
            var expiredAtText = expiredAt.ToString("dd/MM/yyyy HH:mm");

            return $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:720px;margin:24px auto;color:#111;line-height:1.6'>
  <h2 style='margin-top:0'>BÁO GIÁ ĐƠN HÀNG IN ẤN</h2>

  <p>Chào {req.customer_name},</p>
  <p>Chúng tôi gửi đến bạn báo giá cho đơn hàng <b>{productName}</b> với các thông tin như sau:</p>

  <h3>Thông tin yêu cầu</h3>
  <table style='border-collapse:collapse;width:100%;margin-bottom:12px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Mã yêu cầu</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>AM{req.order_request_id:D6}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Ngày yêu cầu</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{requestDate}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Người yêu cầu</b></td>
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

  <h3>Thông tin đơn hàng</h3>
  <table style='border-collapse:collapse;width:100%;margin-bottom:12px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:30%;'><b>Sản phẩm</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{productName}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Số lượng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{quantity}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Ngày giao dự kiến</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{delivery}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Hình thức thiết kế</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{designType}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Giấy sử dụng</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{paperName}</td>
    </tr>   
  </table>

  <h3>Chi tiết chi phí</h3>
  <table style='border-collapse:collapse;width:100%;margin-bottom:16px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:40%;'><b>Tiền nguyên vật liệu</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{VND(materialCost)}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Tiền công</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{VND(laborCost)}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Các phí khác</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{VND(otherFees)}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Phụ thu giao gấp</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{VND(rushAmount)}</td>
    </tr>
  </table>

  <h3>Tổng quan báo giá</h3>
  <table style='border-collapse:collapse;width:100%;margin-bottom:16px;'>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;width:40%;'><b>Giá tổng ban đầu</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>{VND(subtotal)}</td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Giảm giá</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'>
        {discountPercent:N2}% &nbsp; ( - {VND(discountAmount)} )
      </td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Giá sau giảm</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>{VND(finalTotal)}</b></td>
    </tr>
    <tr>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>Số tiền đặt cọc</b></td>
      <td style='border:1px solid transparent;padding:4px 8px;'><b>{VND(deposit)}</b></td>
    </tr>
  </table>

  <p style='margin:16px 0'>
    Bạn có thể vào trang chi tiết đơn hàng để xem đầy đủ thông tin, gửi/ cập nhật file thiết kế
    và thanh toán tiền cọc trực tiếp tại đó:
  </p>

  <p style='text-align:center;margin:18px 0'>
    <a href='{orderDetailUrl}'
       style='display:inline-block;padding:10px 18px;border-radius:6px;
              background:#2563eb;color:#ffffff;text-decoration:none;font-weight:600'>
      Xem chi tiết đơn hàng
    </a>
  </p>

  <div style='margin-top:18px;padding:12px 14px;border-radius:8px;background:#fef3c7;color:#92400e;font-size:13px;'>
    <b>Lưu ý quan trọng:</b><br/>
    Đơn báo giá chỉ có hiệu lực trong vòng <b>24 giờ</b> kể từ thời điểm gửi email
    (đến khoảng <b>{expiredAtText}</b>). Sau thời gian này, các thông tin về giá có thể thay đổi
    và email này sẽ không còn giá trị.
  </div>

  <p style='margin-top:18px;font-size:13px;color:#6b7280'>
    Nếu bạn không thực hiện yêu cầu báo giá này, vui lòng bỏ qua email.
  </p>

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
