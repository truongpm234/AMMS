using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Entities
{
    [Table("machines", Schema = "AMMS_DB")]
    public partial class machine
    {
        public int machine_id { get; set; }

        public string process_name { get; set; } = null!;

        public string machine_code { get; set; } = null!;

        public string? process_code { get; set; }

        public bool is_active { get; set; } = true;

        public int quantity { get; set; } = 1;

        public int capacity_per_hour { get; set; }

        public int capacity_min { get; set; }

        public int? busy_quantity { get; set; }

        public int? free_quantity { get; set; }

        public int capacity_max { get; set; }

        public decimal working_hours_per_day { get; set; } = 24m;

        public decimal efficiency_percent { get; set; } = 85m;

        public string? note { get; set; }

        [NotMapped]
        public decimal daily_capacity =>
            quantity * capacity_per_hour * working_hours_per_day * efficiency_percent / 100m;
    }
}
