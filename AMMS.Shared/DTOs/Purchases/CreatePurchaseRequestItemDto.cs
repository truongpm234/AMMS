using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public class CreatePurchaseRequestItemDto
    {
        public int MaterialId { get; set; }
        public decimal Quantity { get; set; }

        // ✅ item-level supplier (multi supplier)
        public int? SupplierId { get; set; }

        // ✅ NEW: unit price
        public decimal? Price { get; set; }
    }
}
