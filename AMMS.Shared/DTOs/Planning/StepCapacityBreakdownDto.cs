using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Planning
{
    public class StepCapacityBreakdownDto
    {
        public string process_code { get; set; } = "";
        public string process_name { get; set; } = "";
        public string unit { get; set; } = "";
        public decimal required_units { get; set; }
        public decimal backlog_units { get; set; }
        public decimal daily_capacity { get; set; }
        public DateTime start_at { get; set; }
        public DateTime finish_at { get; set; }
        public decimal wait_days { get; set; }
        public decimal duration_days { get; set; }
        public DateTime queue_available_at { get; set; }
        public string? wait_reason { get; set; }
    }
}

