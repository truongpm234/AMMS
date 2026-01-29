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
            string FormatVND(decimal? amount) => string.Format("{0:N0} đ", amount ?? 0);
            return $@"
<!DOCTYPE html>
<html>
<head>
<style>
    body {{ margin: 0; padding: 0; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; color: #333; }}
    .container {{ max-width: 700px; margin: 0 auto; background: #fff; }}
    table {{ border-collapse: collapse; width: 100%; }}
    td {{ vertical-align: top; }}
    
    /* Typography */
    .header-title {{ font-size: 14px; font-weight: 700; text-transform: uppercase; padding-bottom: 8px; margin-bottom: 12px; letter-spacing: 0.5px; }}
    .label {{ color: #718096; font-size: 13px; padding: 8px 0; border-bottom: 1px solid #edf2f7; }}
    .value {{ color: #2d3748; font-weight: 600; font-size: 13px; text-align: right; padding: 8px 0; border-bottom: 1px solid #edf2f7; }}
    
    /* Màu sắc các line tiêu đề */
    .line-blue {{ border-bottom: 2px solid #3182ce; color: #3182ce; }}
    .line-orange {{ border-bottom: 2px solid #dd6b20; color: #dd6b20; }}
    .line-green {{ border-bottom: 2px solid #38a169; color: #38a169; }}

    /* Box style cho bảng giá */
    .cost-box {{ background-color: #fffaf0; border-radius: 6px; padding: 15px; }}
    .total-row td {{ border-bottom: none; padding-top: 12px; font-size: 15px; }}
    .final-price {{ color: #2b6cb0; font-size: 18px; font-weight: 800; }}
</style>
</head>
<body style='background-color: #f7fafc; padding: 30px 0;'>

  <div class='container' style='border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px rgba(0,0,0,0.05);'>
    
    <div style='background: linear-gradient(90deg, #2b6cb0 0%, #2c5282 100%); padding: 25px 30px;'>
      <table border='0' cellpadding='0' cellspacing='0'>
        <tr>
          <td>
            <div style='color: #bee3f8; font-size: 11px; font-weight: bold; letter-spacing: 1px;'>MES SYSTEM</div>
            <div style='color: #fff; font-size: 22px; font-weight: 800; margin-top: 5px;'>BÁO GIÁ ĐƠN HÀNG</div>
          </td>
          <td align='right'>
            <span style='background: rgba(255,255,255,0.15); color: #fff; padding: 6px 12px; border-radius: 4px; font-size: 14px; font-weight: bold;'>
              AM{req.order_request_id:D6}
            </span>
          </td>
        </tr>
      </table>
    </div>

    <div style='padding: 30px;'>
      
      <div style='margin-bottom: 30px;'>
        <p style='margin: 0; font-size: 15px;'>Chào <b>{req.customer_name}</b>,</p>
        <p style='margin: 5px 0 0; color: #718096; font-size: 14px;'>Dưới đây là chi tiết báo giá cho yêu cầu in ấn của bạn:</p>
      </div>

      <table border='0' cellpadding='0' cellspacing='0'>
        
        <tr>
          <td width='48%' style='padding-right: 20px;'>
            <div class='header-title line-blue'>Thông tin đơn hàng</div>
            <table width='100%'>
              <tr><td class='label'>Ngày yêu cầu</td><td class='value'>{requestDateText}</td></tr>
              <tr><td class='label'>Người yêu cầu</td><td class='value' style='text-transform:uppercase'>{req.customer_name}</td></tr>
              <tr><td class='label'>Số điện thoại</td><td class='value'>{req.customer_phone}</td></tr>
              <tr><td class='label'>Email</td><td class='value' style='color:#3182ce'>{req.customer_email}</td></tr>
            </table>
          </td>

          <td width='48%' style='padding-left: 20px;'>
             <div class='header-title line-orange'>Bảng kê chi phí</div>
             <div class='cost-box'>
                <table width='100%'>
                  <tr>
                    <td style='padding:6px 0; color:#4a5568; font-size:13px;'>Nguyên vật liệu</td>
                    <td style='padding:6px 0; text-align:right; font-weight:700; color:#2d3748; font-size:13px;'>{FormatVND(materialCost)}</td>
                  </tr>
                  <tr>
                    <td style='padding:6px 0; color:#4a5568; font-size:13px;'>Chi phí nhân công</td>
                    <td style='padding:6px 0; text-align:right; font-weight:700; color:#2d3748; font-size:13px;'>{FormatVND(laborCost)}</td>
                  </tr>
                  <tr>
                    <td style='padding:6px 0; color:#4a5568; font-size:13px;'>Chi phí khác</td>
                    <td style='padding:6px 0; text-align:right; font-weight:700; color:#2d3748; font-size:13px;'>{FormatVND(otherFees)}</td>
                  </tr>
                   <tr>
                    <td style='padding:6px 0; color:#4a5568; font-size:13px;'>Phụ thu giao gấp</td>
                    <td style='padding:6px 0; text-align:right; font-weight:700; color:#2d3748; font-size:13px;'>{FormatVND(rushAmount)}</td>
                  </tr>
                </table>
             </div>
          </td>
        </tr>

        <tr>
            <td colspan='2' style='height: 40px;'></td>
        </tr>

        <tr>
          <td width='48%' style='padding-right: 20px;'>
            <div class='header-title line-blue'>Chi tiết sản phẩm</div>
            <table width='100%'>
              <tr><td class='label'>Sản phẩm</td><td class='value'>{productName}</td></tr>
              <tr><td class='label'>Số lượng</td><td class='value'>{quantity:N0}</td></tr>
              <tr><td class='label'>Loại giấy</td><td class='value'>{paperName}</td></tr>
              <tr><td class='label'>Thiết kế</td><td class='value'>{designType}</td></tr>
              <tr><td class='label'>Giao dự kiến</td><td class='value'>{delivery}</td></tr>
            </table>
          </td>

          <td width='48%' style='padding-left: 20px;'>
            <div class='header-title line-green'>Tổng thanh toán</div>
            <table width='100%'>
              <tr>
                <td class='label' style='border:none; padding-bottom:4px'>Tạm tính</td>
                <td class='value' style='border:none; padding-bottom:4px'>{FormatVND(subtotal)}</td>
              </tr>
              <tr>
                <td class='label' style='border-bottom:1px dashed #cbd5e1'>Giảm giá ({discountPercent:0.#}%)</td>
                <td class='value' style='border-bottom:1px dashed #cbd5e1; color:#e53e3e'>- {FormatVND(discountAmount)}</td>
              </tr>
              <tr class='total-row'>
                <td style='font-weight:700; color:#2d3748;'>THÀNH TIỀN</td>
                <td align='right' class='final-price'>{FormatVND(finalTotal)}</td>
              </tr>
              <tr>
                <td colspan='2' style='text-align:right; font-size:11px; color:#e53e3e; padding-top:4px;'>(Đã bao gồm VAT)</td>
              </tr>
            </table>
            <div style='margin-top: 20px; background: #f0fff4; border: 1px solid #9ae6b4; border-radius: 6px; padding: 12px;'>
                <table width='100%'>
                    <tr>
                        <td style='color: #276749; font-weight: 700; font-size: 13px;'>Cần đặt cọc trước:</td>
                        <td align='right' style='color: #2f855a; font-weight: 800; font-size: 16px;'>{FormatVND(deposit)}</td>
                    </tr>
                </table>
            </div>
          </td>
        </tr>

      </table>

      <div style='text-align: center; margin-top: 40px; padding-top: 30px; border-top: 1px solid #edf2f7;'>
         <a href='{orderDetailUrl}' style='background-color: #3182ce; color: white; padding: 12px 30px; text-decoration: none; font-weight: bold; border-radius: 50px; display: inline-block; box-shadow: 0 4px 6px rgba(49, 130, 206, 0.3);'>
            Xem chi tiết & Thanh toán
         </a>
         <p style='margin-top: 20px; font-size: 12px; color: #a0aec0;'>
            Báo giá có hiệu lực đến {expiredAtText}. Sau thời gian này, mọi thông tin về đơn giá và chi phí có thể thay đổi. Mọi thao tác thanh toán sau thời gian này đều sẽ không được ghi nhận, mọi thắc mắc vui lòng liên hệ lại với chúng tôi để được hỗ trợ.
         </p>
      </div>

    </div>
    
    <div style='background-color: #edf2f7; padding: 15px; text-align: center; font-size: 12px; color: #718096;'>
        Email này được gửi tự động từ hệ thống MES.
    </div>

  </div>
</body>
</html>";
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

  <p>MES trân trọng!</p>
</div>";
        }

        public static string AcceptConsultantEmail(order_request req, order order)
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
