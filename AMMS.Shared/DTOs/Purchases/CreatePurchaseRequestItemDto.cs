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
    }
}
