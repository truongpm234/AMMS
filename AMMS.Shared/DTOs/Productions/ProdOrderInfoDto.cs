using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ProdOrderInfoDto
    {
        public int prod_order_id { get; set; }

        public int group_prod_id { get; set; }

        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int? single_prod_id { get; set; }

        public int qty { get; set; }

        public int? product_type_id { get; set; }

        public string? product_process { get; set; }

        public string? status { get; set; }

        public DateTime? created_at { get; set; }
    }
}
