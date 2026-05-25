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
        public bool? is_full_process { get; set; }
        public int? sub_product_id { get; set; }
        public int sub_product_used_qty { get; set; }
        public int nvl_qty { get; set; }
        public int order_quantity { get; set; }
        public string? gm_note { get; set; }
        public string? mgr_note { get; set; }
        public string production_method { get; set; } = "";
        public string message { get; set; } = "";
        public string? production_approval_flow { get; set; }

        public bool is_auto_production_approval { get; set; }
        public string? sub_product_issue_file { get; set; }
        public string? production_approval_label { get; set; }
    }
}
