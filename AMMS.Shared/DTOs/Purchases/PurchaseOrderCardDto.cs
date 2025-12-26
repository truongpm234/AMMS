using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public class PurchaseOrderCardDto
    {
        public int PurchaseId { get; set; }
        public string? Code { get; set; }

        public int? SupplierId { get; set; }
        public string SupplierName { get; set; } = null!;

        public DateTime? EtaDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedByName { get; set; } = null!;

        // ✅ 3 status chuẩn
        public string Status { get; set; } = null!; // Pending | Shipping | Received

        public DateTime? ReceivedAt { get; set; }
        public string? ReceivedByName { get; set; }

        public decimal TotalQty { get; set; }

        public List<PurchaseOrderItemDto> Items { get; set; } = new();
    }

}
