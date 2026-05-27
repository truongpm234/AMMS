using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class GroupProductionCandidateDto
    {
        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int? single_prod_id { get; set; }

        public int? product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public string? product_name { get; set; }

        public int quantity { get; set; }

        public string? production_process { get; set; }

        public string process_key { get; set; } = "";

        public DateTime? delivery_date { get; set; }

        public bool can_group { get; set; }

        public string? reason { get; set; }
        public string? production_method { get; set; }
    }
}
