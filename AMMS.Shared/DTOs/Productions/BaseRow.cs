using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public sealed class BaseRow
    {
        public int prod_id { get; set; }

        public int order_id { get; set; }

        public string? code { get; set; }

        public DateTime? delivery_date { get; set; }

        public int? product_type_id { get; set; }

        public string? status { get; set; }

        public string? customer_name { get; set; }

        public string? first_item_product_name { get; set; }

        public string? first_item_production_process { get; set; }

        public int? first_item_quantity { get; set; }

        public string? production_status { get; set; } 

        public string? order_status { get; set; }      


    }
}
