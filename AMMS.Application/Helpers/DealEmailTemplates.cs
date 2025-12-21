using AMMS.Infrastructure.Entities;
using System.Globalization;
using System.Text;

namespace AMMS.Application.Helpers
{
    public static class DealEmailTemplates
    {
        private static string VND(decimal v)
            => string.Format(new CultureInfo("vi-VN"), "{0:N0} ₫", v);

        public static string QuoteEmail(
    order_request req,
    cost_estimate est,
    string acceptUrl,
    string rejectUrl)
        {
            string VND(decimal v) =>
                string.Format(new System.Globalization.CultureInfo("vi-VN"), "{0:N0} ₫", v);

            return $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:700px;margin:auto;color:#333'>

  <h2 style='margin-bottom:5px'>BÁO GIÁ ĐƠN HÀNG IN ẤN</h2>
  <p style='margin-top:0;color:#666'>
    AMMS System – Báo giá chi tiết
  </p>

  <hr style='border:none;border-top:1px solid #eee;margin:20px 0'/>

  <table width='100%' style='font-size:14px'>
    <tr>
      <td><b>Sản phẩm:</b></td>
      <td>{req.product_name}</td>
    </tr>
    <tr>
      <td><b>Số lượng:</b></td>
      <td>{req.quantity}</td>
    </tr>
    <tr>
      <td><b>Giao hàng dự kiến:</b></td>
      <td>{req.delivery_date:dd/MM/yyyy}</td>
    </tr>
  </table>

  <h3 style='margin-top:30px'>Chi tiết chi phí</h3>

  <table width='100%' cellpadding='10' cellspacing='0'
         style='border-collapse:collapse;font-size:14px;border:1px solid #eee'>

    <tr>
      <td>Giấy ({est.paper_sheets_used} tờ)</td>
      <td align='right'>{VND(est.paper_cost)}</td>
    </tr>

    <tr style='background:#fafafa'>
      <td>Mực in</td>
      <td align='right'>{VND(est.ink_cost)}</td>
    </tr>

    <tr>
      <td>Keo phủ</td>
      <td align='right'>{VND(est.coating_glue_cost)}</td>
    </tr>

    <tr style='background:#fafafa'>
      <td>Keo bồi</td>
      <td align='right'>{VND(est.mounting_glue_cost)}</td>
    </tr>

    <tr>
      <td>Màng cán</td>
      <td align='right'>{VND(est.lamination_cost)}</td>
    </tr>

    <tr>
      <td><b>Tổng vật liệu</b></td>
      <td align='right'><b>{VND(est.material_cost)}</b></td>
    </tr>

    <tr style='background:#fafafa'>
      <td>Khấu hao</td>
      <td align='right'>{VND(est.overhead_cost)}</td>
    </tr>

    <tr>
      <td>Rush</td>
      <td align='right'>{VND(est.rush_amount)}</td>
    </tr>

    <tr style='background:#fafafa'>
      <td>Chiết khấu</td>
      <td align='right'>-{VND(est.discount_amount)}</td>
    </tr>

    <tr>
      <td style='padding-top:15px'><b>TỔNG THANH TOÁN</b></td>
      <td align='right' style='padding-top:15px'>
        <span style='font-size:18px;color:#d0021b'><b>{VND(est.final_total_cost)}</b></span>
      </td>
    </tr>
  </table>

  <div style='margin:30px 0;text-align:center'>
    <a href='{acceptUrl}'
       style='display:inline-block;padding:12px 22px;
              background:#28a745;color:white;
              text-decoration:none;border-radius:4px;
              font-weight:bold'>
      ĐỒNG Ý BÁO GIÁ
    </a>

    <a href='{rejectUrl}'
       style='display:inline-block;padding:12px 22px;
              background:#dc3545;color:white;
              text-decoration:none;border-radius:4px;
              margin-left:10px'>
      TỪ CHỐI
    </a>
  </div>

  <p style='font-size:13px;color:#666;text-align:center'>
    Nếu cần chỉnh sửa hoặc tư vấn thêm, vui lòng phản hồi email này.
  </p>

</div>
";
        }


        public static string AcceptCustomerEmail(
            order_request req,
            order order,
            cost_estimate est,
            string trackingUrl)
        {
            return $@"
<h2>ĐƠN HÀNG ĐÃ ĐƯỢC PHÊ DUYỆT</h2>

<p>Cảm ơn bạn đã xác nhận báo giá.</p>

<p><b>Mã đơn hàng:</b> <span style='color:blue'>{order.code}</span></p>
<p><b>Sản phẩm:</b> {req.product_name}</p>
<p><b>Số lượng:</b> {req.quantity}</p>
<p><b>Giá trị đơn hàng:</b> {VND(est.final_total_cost)}</p>

<hr/>

<p>
Bạn có thể theo dõi tiến độ sản xuất tại:
<br/>
<a href='{trackingUrl}'>{trackingUrl}</a>
</p>

<p>
Vui lòng lưu lại <b>mã đơn hàng</b> để tra cứu sau này.
</p>

<p>AMMS trân trọng!</p>
";
        }

        public static string AcceptConsultantEmail(
            order_request req,
            order order)
        {
            return $@"
<h3>KHÁCH HÀNG ĐÃ ĐỒNG Ý BÁO GIÁ</h3>

<p>Request ID: {req.order_request_id}</p>
<p>Order Code: <b>{order.code}</b></p>
<p>Sản phẩm: {req.product_name}</p>
<p>Số lượng: {req.quantity}</p>
";
        }
    }
}
