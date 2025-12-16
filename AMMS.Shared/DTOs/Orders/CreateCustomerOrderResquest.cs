using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class CreateCustomerOrderResquest
    {
        public string customer_name { get; set; } = null!;

        public string? customer_phone { get; set; }

        public string? customer_email { get; set; }

        public DateTime delivery_date { get; set; }

        public string product_name { get; set; } = null!;

        public string? description { get; set; }

        public int quantity { get; set; }

        public string? design_file_path { get; set; }
    }
}
