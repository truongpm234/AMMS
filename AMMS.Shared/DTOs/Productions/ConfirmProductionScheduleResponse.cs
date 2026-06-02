using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class ConfirmProductionScheduleResponse
    {
        public bool success { get; set; }

        public int prod_id { get; set; }

        public int? order_id { get; set; }

        public string? production_code { get; set; }

        public string? prod_kind { get; set; }

        public string? production_method { get; set; }

        public DateTime? planned_start_date { get; set; }

        public DateTime? planned_end_date { get; set; }

        public string? issue_file { get; set; }

        public string message { get; set; } = "";
    }
}
