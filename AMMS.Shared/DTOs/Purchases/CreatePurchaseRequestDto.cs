using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Purchases
{
    public class CreatePurchaseRequestDto
    {
        public int? supplier_id { get; set; }     
        public List<CreatePurchaseRequestItemDto> Items { get; set; } = new();
    }
}
