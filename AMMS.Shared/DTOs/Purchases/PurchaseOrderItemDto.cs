using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public class PurchaseOrderItemDto
    {
        public int Id { get; set; }
        public int MaterialId { get; set; }
        public string MaterialCode { get; set; } = null!;
        public string MaterialName { get; set; } = null!;
        public decimal QtyOrdered { get; set; }
        public string Unit { get; set; } = null!;
    }

}
