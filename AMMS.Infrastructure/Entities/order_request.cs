using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Entities
{
    public partial class order_request
    {
        public int order_request_id { get; set; }

        public string code { get; set; } = null!;

        public int? quote_id { get; set; }

        public int? customer_id { get; set; }

        public int? consultant_id { get; set; }

        public DateTime? order_request_date { get; set; }

        public decimal? total_amount { get; set; }

        public string? status { get; set; }


    }

}

