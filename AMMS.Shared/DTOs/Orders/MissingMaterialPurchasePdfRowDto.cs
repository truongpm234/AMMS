using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class MissingMaterialPurchasePdfRowDto
    {
        public long miss_id { get; set; }
        public int material_id { get; set; }
        public string material_name { get; set; } = "";
        public decimal quantity { get; set; }
        public decimal total_price { get; set; }
    }
}
