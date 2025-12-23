using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderDetailDto
    {
        // Header
        public int OrderId { get; set; }
        public string Code { get; set; } = null!;
        public string Status { get; set; } = null!;
        public string PaymentStatus { get; set; } = null!;
        public DateTime OrderDate { get; set; }              // Ngày tạo
        public DateTime? DeliveryDate { get; set; }          // Ngày giao

        // Khách hàng
        public string CustomerName { get; set; } = string.Empty;   // "Tập đoàn FPT"
        public string? CustomerEmail { get; set; }                 // Email khách hàng
        public string? CustomerPhone { get; set; }                 // SĐT

        // Sản phẩm
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }

        // Lịch sản xuất
        public DateTime? ProductionStartDate { get; set; }   // Bắt đầu
        public DateTime? ProductionEndDate { get; set; }     // Kết thúc
        public string ApproverName { get; set; } = string.Empty; // Người duyệt: Quản lý sản xuất

        // Thông tin chi tiết
        public string? Specification { get; set; }           // Quy cách
        public string? Note { get; set; }                    // Ghi chú

        // Tài chính
        public decimal RushAmount { get; set; }              // Phí gấp
        public decimal EstimateTotal { get; set; }           // Tổng báo giá / thành tiền

        // File
        public string? SampleFileUrl { get; set; }           // File mẫu (null => "Chưa có file mẫu")
        public string? ContractFileUrl { get; set; }         // Hợp đồng (null => "Chưa có hợp đồng")
    }
}
