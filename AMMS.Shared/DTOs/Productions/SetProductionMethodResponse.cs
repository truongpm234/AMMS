using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class SetProductionMethodResponse
    {
        public bool success { get; set; }
        public int order_id { get; set; }
        public int prod_id { get; set; }

        public bool is_full_process { get; set; }
        public int? sub_product_id { get; set; }
        public int sub_product_used_qty { get; set; }

        public int order_quantity { get; set; }

        public string production_method { get; set; } = "";
        public string message { get; set; } = "";
    }
}
