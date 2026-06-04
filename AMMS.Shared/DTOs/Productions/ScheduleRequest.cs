using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public sealed class ScheduleRequest
    {
        public int order_id { get; set; }
        public int product_type_id { get; set; }
        public string? production_processes { get; set; }
        public int? manager_id { get; set; }
        public bool? is_priority { get; set; } = false;
    }
}

