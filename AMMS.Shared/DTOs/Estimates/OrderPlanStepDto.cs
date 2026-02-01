using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class OrderPlanStepDto
    {
        public int seq { get; set; }
        public string process_code { get; set; } = "";
        public string process_name { get; set; } = "";
        public string machine_code { get; set; } = "";
        public decimal qty_work { get; set; }
        public int est_minutes { get; set; }
        public DateTime planned_start { get; set; }
        public DateTime planned_end { get; set; }
    }
}
