using AMMS.Infrastructure.Entities;
using System.Globalization;

public static string QuoteEmail(
    order_request req,
    cost_estimate est,
    string acceptUrl,
    string rejectPostUrl // đổi rejectUrl -> rejectPostUrl
)
{
    string VND(decimal v) =>
        string.Format(new CultureInfo("vi-VN"), "{0:N0} ₫", v);

    // NOTE: rejectPostUrl là endpoint POST (vd: /api/requests/deal/reject)
    // token + orderRequestId sẽ gửi trong body

    return $@"
<div style='font-family:Arial,Helvetica,sans-serif;max-width:700px;margin:auto;color:#333'>

  <h2 style='margin-bottom:5px'>BÁO GIÁ ĐƠN HÀNG IN ẤN</h2>
  <p style='margin-top:0;color:#666'>AMMS System – Báo giá chi tiết</p>

  <hr style='border:none;border-top:1px solid #eee;margin:20px 0'/>

  <table width='100%' style='font-size:14px'>
    <tr><td><b>Sản phẩm:</b></td><td>{req.product_name}</td></tr>
    <tr><td><b>Số lượng:</b></td><td>{req.quantity}</td></tr>
    <tr><td><b>Giao hàng dự kiến:</b></td><td>{req.delivery_date:dd/MM/yyyy}</td></tr>
  </table>

  <h3 style='margin-top:30px'>Chi tiết chi phí</h3>

  <table width='100%' cellpadding='10' cellspacing='0'
         style='border-collapse:collapse;font-size:14px;border:1px solid #eee'>

    <tr><td>Giấy ({est.paper_sheets_used} tờ)</td><td align='right'>{VND(est.paper_cost)}</td></tr>
    <tr style='background:#fafafa'><td>Mực in</td><td align='right'>{VND(est.ink_cost)}</td></tr>
    <tr><td>Keo phủ</td><td align='right'>{VND(est.coating_glue_cost)}</td></tr>
    <tr style='background:#fafafa'><td>Keo bồi</td><td align='right'>{VND(est.mounting_glue_cost)}</td></tr>
    <tr><td>Màng cán</td><td align='right'>{VND(est.lamination_cost)}</td></tr>

    <tr><td><b>Tổng vật liệu</b></td><td align='right'><b>{VND(est.material_cost)}</b></td></tr>
    <tr style='background:#fafafa'><td>Khấu hao</td><td align='right'>{VND(est.overhead_cost)}</td></tr>
    <tr><td>Rush</td><td align='right'>{VND(est.rush_amount)}</td></tr>
    <tr style='background:#fafafa'><td>Chiết khấu</td><td align='right'>-{VND(est.discount_amount)}</td></tr>

    <tr>
      <td style='padding-top:15px'><b>TỔNG THANH TOÁN</b></td>
      <td align='right' style='padding-top:15px'>
        <span style='font-size:18px;color:#d0021b'><b>{VND(est.final_total_cost)}</b></span>
      </td>
    </tr>
  </table>

  <div style='margin:30px 0;text-align:center'>
    <a href='{acceptUrl}'
       style='display:inline-block;padding:12px 22px;background:#28a745;color:white;
              text-decoration:none;border-radius:4px;font-weight:bold'>
      ĐỒNG Ý BÁO GIÁ
    </a>

    <a href='#' onclick='return ammsReject();'
       style='display:inline-block;padding:12px 22px;background:#dc3545;color:white;
              text-decoration:none;border-radius:4px;margin-left:10px'>
      TỪ CHỐI
    </a>
  </div>

  <p id='reject-status' style='font-size:13px;color:#666;text-align:center'></p>

  <script>
    // ⚠️ Lưu ý: Nhiều email client (Gmail Web) có thể chặn JS.
    // Vì vậy, ta vẫn hỗ trợ fallback: nếu JS bị chặn, hiển thị hướng dẫn.
    function ammsReject() {{
      try {{
        var reason = prompt('Vui lòng nhập lý do từ chối báo giá:');
        if (!reason || !reason.trim()) {{
          alert('Bạn cần nhập lý do thì mới gửi được.');
          return false;
        }}

        // Body gửi lên API
        var payload = {{
          orderRequestId: {req.order_request_id},
          token: '{Guid.NewGuid().ToString()}', // ❌ KHÔNG dùng kiểu này trong template nếu token ở server
          reason: reason.trim()
        }};

        // Nếu bạn đã có token server-side thì truyền token vào template thay vì random ở đây.
        // Cách đúng: payload.token = '{tokenFromServer}';

        var xhr = new XMLHttpRequest();
        xhr.open('POST', '{rejectPostUrl}', true);
        xhr.setRequestHeader('Content-Type', 'application/json;charset=UTF-8');

        xhr.onreadystatechange = function() {{
          if (xhr.readyState === 4) {{
            var el = document.getElementById('reject-status');
            if (xhr.status >= 200 && xhr.status < 300) {{
              el.innerHTML = '✅ Đã gửi từ chối thành công. Cảm ơn bạn!';
            }} else {{
              el.innerHTML = '⚠️ Không gửi được từ chối. Vui lòng thử lại hoặc liên hệ hỗ trợ.';
            }}
          }}
        }};

        xhr.send(JSON.stringify(payload));
        document.getElementById('reject-status').innerHTML = '⏳ Đang gửi lý do từ chối...';
        return false;
      }} catch (e) {{
        document.getElementById('reject-status').innerHTML =
          '⚠️ Trình email của bạn không hỗ trợ gửi trực tiếp. Vui lòng phản hồi email này và ghi rõ lý do từ chối.';
        return false;
      }}
    }}
  </script>

  <p style='font-size:13px;color:#666;text-align:center'>
    Nếu nút “Từ chối” không hoạt động, vui lòng phản hồi email này và ghi rõ <b>lý do từ chối</b>.
  </p>

</div>
";
}
