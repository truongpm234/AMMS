using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class OrdersByProcessDto
    {
        public int order_id { get; set; }
        public string code { get; set; } = "";
        public int? quote_id { get; set; }
        public DateTime? order_date { get; set; }
        public DateTime? delivery_date { get; set; }
        public decimal? total_amount { get; set; }
        public string? status { get; set; }
        public string? payment_status { get; set; }
        public int? production_id { get; set; }
        public bool? is_enough { get; set; }
        public bool? is_buy { get; set; }
        public bool? layout_confirmed { get; set; }
        public bool is_production_ready { get; set; }
        public DateTime? confirmed_delivery_at { get; set; }

        public List<ProductionByProcessDto> productions { get; set; } = new();
    }
}
