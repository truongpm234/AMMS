using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public class CreatePurchaseRequestItemDto
    {
        public int material_id { get; set; }
        public decimal quantity { get; set; }

        // ✅ item-level supplier (multi supplier)
        public int? supplier_id { get; set; }

        // ✅ NEW: unit price
        public decimal? price { get; set; }
    }
}
