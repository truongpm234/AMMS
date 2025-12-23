using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrderListDto
    {
        public int OrderId { get; set; }
        public string Code { get; set; } = "";
        public DateTime? OrderDate { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public string? Status { get; set; }
        public string? PaymentStatus { get; set; }
        public int? QuoteId { get; set; }
        public decimal? TotalAmount { get; set; }
    }
}
