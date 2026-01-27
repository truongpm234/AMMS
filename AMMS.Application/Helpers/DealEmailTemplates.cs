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
            var requestDateText = req.order_request_date?.ToString("dd/MM/yyyy HH:mm") ?? "N/A";
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
<div style='background:#f3f4f6;padding:24px 0;margin:0'>
  <div style='max-width:720px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#0f172a;line-height:1.6'>

    <!-- Header -->
    <div style='background:linear-gradient(135deg,#2563eb,#1d4ed8);border-radius:14px;padding:18px 20px;box-shadow:0 10px 24px rgba(2,6,23,.15)'>
      <div style='display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap'>
        <div>
          <div style='font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#dbeafe;font-weight:700'>
            AMMS • Báo giá đơn hàng in ấn
          </div>
          <div style='font-size:22px;font-weight:800;color:#ffffff;margin-top:4px'>
            BÁO GIÁ ĐƠN HÀNG IN ẤN
          </div>
        </div>

        <div style='background:rgba(255,255,255,.14);border:1px solid rgba(255,255,255,.18);padding:10px 12px;border-radius:12px'>
          <div style='font-size:12px;color:#e0f2fe;font-weight:700'>Mã yêu cầu</div>
          <div style='font-size:18px;color:#ffffff;font-weight:800;letter-spacing:.04em'>AM{req.order_request_id:D6}</div>
        </div>
      </div>
    </div>

    <!-- Body card -->
    <div style='background:#ffffff;border-radius:14px;margin-top:14px;box-shadow:0 10px 26px rgba(2,6,23,.08);overflow:hidden'>
      
      <!-- Greeting -->
      <div style='padding:18px 20px;border-bottom:1px solid #e5e7eb;background:#ffffff'>
        <p style='margin:0;font-size:15px'>Chào <b>{req.customer_name}</b>,</p>
        <p style='margin:8px 0 0 0;color:#334155'>
          Chúng tôi gửi đến bạn báo giá cho đơn hàng <b>{productName}</b> với các thông tin như sau:
        </p>
      </div>

      <!-- Section: Thông tin yêu cầu -->
      <div style='padding:20px 22px'>
        <div style='display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap;margin-bottom:10px'>
          <div style='font-size:16px;font-weight:800;color:#0f172a'>Thông tin yêu cầu</div>
          <div style='font-size:12px;font-weight:700;color:#1d4ed8;background:#eff6ff;border:1px solid #dbeafe;padding:6px 10px;border-radius:999px'>
            Ngày yêu cầu: {requestDateText}

          </div>
        </div>

        <table style='width:100%;border-collapse:separate;border-spacing:0;overflow:hidden;border:1px solid #e5e7eb;border-radius:12px'>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;width:32%;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Mã yêu cầu</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;color:#0f172a;font-weight:800'>AM{req.order_request_id:D6}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Người yêu cầu</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb'>{req.customer_name}</td>
          </tr>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>SĐT</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb'>{req.customer_phone}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Email</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb'>{req.customer_email}</td>
          </tr>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;font-weight:700;color:#334155'>Địa chỉ</td>
            <td style='padding:10px 12px'>{address}</td>
          </tr>
        </table>
      </div>

      <!-- Section: Thông tin đơn hàng -->
      <div style='padding:0 20px 18px 20px'>
        <div style='font-size:16px;font-weight:800;color:#0f172a;margin-bottom:10px'>Thông tin đơn hàng</div>

        <table style='width:100%;border-collapse:separate;border-spacing:0;overflow:hidden;border:1px solid #e5e7eb;border-radius:12px'>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;width:32%;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Sản phẩm</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;font-weight:700'>{productName}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Số lượng</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb'>{quantity}</td>
          </tr>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Ngày giao dự kiến</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb'>{delivery}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Hình thức thiết kế</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb'>{designType}</td>
          </tr>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;font-weight:700;color:#334155'>Giấy sử dụng</td>
            <td style='padding:10px 12px'>{paperName}</td>
          </tr>
        </table>
      </div>

      <!-- Section: Chi tiết chi phí -->
      <div style='padding:0 20px 18px 20px'>
        <div style='font-size:16px;font-weight:800;color:#0f172a;margin-bottom:10px'>Chi tiết chi phí</div>

        <table style='width:100%;border-collapse:separate;border-spacing:0;overflow:hidden;border:1px solid #e5e7eb;border-radius:12px'>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;width:40%;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Tiền nguyên vật liệu</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;color:#0f172a;font-weight:700'>{VND(materialCost)}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Tiền công</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;color:#0f172a;font-weight:700'>{VND(laborCost)}</td>
          </tr>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Các phí khác</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;color:#0f172a;font-weight:700'>{VND(otherFees)}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155'>Phụ thu giao gấp</td>
            <td style='padding:10px 12px;color:#0f172a;font-weight:700'>{VND(rushAmount)}</td>
          </tr>
        </table>
      </div>

      <!-- Section: Tổng quan báo giá -->
      <div style='padding:0 20px 18px 20px'>
        <div style='display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap;margin-bottom:10px'>
          <div style='font-size:16px;font-weight:800;color:#0f172a'>Tổng quan báo giá</div>
          <div style='font-size:12px;font-weight:700;color:#0f766e;background:#ecfdf5;border:1px solid #a7f3d0;padding:6px 12px;border-radius:999px'>
            Hiệu lực đến: {expiredAtText}
          </div>
        </div>

        <table style='width:100%;border-collapse:separate;border-spacing:0;overflow:hidden;border:1px solid #e5e7eb;border-radius:12px'>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;width:40%;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Giá tổng ban đầu</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;color:#0f172a;font-weight:700'>{VND(subtotal)}</td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:700;color:#334155;border-bottom:1px solid #e5e7eb'>Giảm giá</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;color:#0f172a;font-weight:700'>
              {discountPercent:N2}% &nbsp; ( - {VND(discountAmount)} )
            </td>
          </tr>
          <tr style='background:#f8fafc'>
            <td style='padding:10px 12px;font-weight:800;color:#0f172a;border-bottom:1px solid #e5e7eb'>Giá sau giảm</td>
            <td style='padding:10px 12px;border-bottom:1px solid #e5e7eb;font-weight:900;color:#1d4ed8;font-size:16px'>
              {VND(finalTotal)}
            </td>
          </tr>
          <tr>
            <td style='padding:10px 12px;font-weight:800;color:#0f172a'>Số tiền đặt cọc</td>
            <td style='padding:10px 12px;font-weight:900;color:#b45309;font-size:16px'>
              {VND(deposit)}
            </td>
          </tr>
        </table>
      </div>

      <!-- CTA -->
      <div style='padding:18px 20px;background:#f8fafc;border-top:1px solid #e5e7eb'>
        <p style='margin:0 0 12px 0;color:#334155'>
          Bạn có thể vào trang chi tiết đơn hàng để xem đầy đủ thông tin, gửi/ cập nhật file thiết kế
          và thanh toán tiền cọc trực tiếp tại đó:
        </p>

        <div style='text-align:center;margin:14px 0'>
          <a href='{orderDetailUrl}'
             style='display:inline-block;padding:12px 18px;border-radius:10px;
                    background:#2563eb;color:#ffffff;text-decoration:none;font-weight:800;
                    box-shadow:0 10px 20px rgba(37,99,235,.25)'>
            Xem chi tiết đơn hàng
          </a>
        </div>

        <div style='margin-top:12px;padding:14px 14px;border-radius:12px;background:#fffbeb;border:1px solid #fde68a;color:#92400e;font-size:13px'>
          <div style='font-weight:900;margin-bottom:6px'>Lưu ý quan trọng:</div>
          <div>
            Đơn báo giá chỉ có hiệu lực trong vòng <b>24 giờ</b> kể từ thời điểm gửi email
            (đến khoảng <b>{expiredAtText}</b>). Sau thời gian này, các thông tin về giá có thể thay đổi
            và email này sẽ không còn giá trị.
          </div>
        </div>

        <p style='margin:14px 0 0 0;font-size:13px;color:#64748b'>
          Nếu bạn không thực hiện yêu cầu báo giá này, vui lòng bỏ qua email.
        </p>
      </div>

      <!-- Footer -->
      <div style='padding:14px 20px;background:#ffffff;border-top:1px solid #e5e7eb'>
        <p style='margin:0'>Trân trọng,<br/><b>Đội ngũ AMMS</b></p>
        <p style='margin:8px 0 0 0;font-size:12px;color:#94a3b8'>
          Email này được gửi tự động từ hệ thống AMMS. Vui lòng không trả lời trực tiếp email này.
        </p>
      </div>

    </div>
  </div>
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
