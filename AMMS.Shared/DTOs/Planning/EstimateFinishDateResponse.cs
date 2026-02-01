using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Planning
{
    public class EstimateFinishDateResponse
    {
        public int order_request_id { get; set; }
        public DateTime? desired_delivery_date { get; set; }
        public DateTime estimated_finish_date { get; set; }
        public bool can_meet_desired_date { get; set; }
        public int days_late_if_any { get; set; }
        public int days_early_if_any { get; set; }
        public List<StepCapacityBreakdownDto> steps { get; set; } = new();
    }
}
