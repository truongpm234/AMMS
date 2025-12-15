using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities
{
    public partial class order_request
    {
        public int order_request_id { get; set; }

        public string customer_name { get; set; } = null!;

        public string? customer_phone { get; set; }

        public string? customer_email { get; set; }

        public DateTime delivery_date { get; set; }

        public string product_name { get; set; } = null!;

        public string? description { get; set; }

        public int quantity { get; set; }

        public string?  design_file_path { get; set; }

        public DateTime order_request_date { get; set; }

    }

}

