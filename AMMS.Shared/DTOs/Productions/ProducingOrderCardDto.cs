using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ProducingOrderCardDto
    {
        public int order_id { get; set; }

        public string? code { get; set; }

        public string customer_name { get; set; } = "";

        public string? product_name { get; set; }

        public int quantity { get; set; }

        public DateTime? delivery_date { get; set; }

        public decimal progress_percent { get; set; }

        public string? current_stage { get; set; }

        public List<string> stages { get; set; } = new();

        public string? production_status { get; set; }
    }
}
