using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class OrderCompletionEstimateResponse
    {
        public int order_id { get; set; }
        public DateTime now { get; set; }
        public DateTime desired_delivery_date { get; set; }
        public DateTime estimated_finish_date { get; set; }
        public bool can_meet_deadline { get; set; }
        public int total_est_minutes { get; set; }

        public List<OrderPlanStepDto> steps { get; set; } = new();
    }
}
