using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations.Schema;

namespace AMMS.Infrastructure.Entities
{
    [Table("cost_estimate_process", Schema = "AMMS_DB")]
    public partial class cost_estimate_process
    {
        public int process_cost_id { get; set; }

        public int estimate_id { get; set; }

        public string process_code { get; set; } = null!;

        public string? process_name { get; set; }

        public decimal quantity { get; set; }

        public string? unit { get; set; }

        public decimal unit_price { get; set; }

        public decimal total_cost { get; set; }

        public string? note { get; set; }

        public DateTime created_at { get; set; }

        public virtual cost_estimate estimate { get; set; } = null!;
    }
}

