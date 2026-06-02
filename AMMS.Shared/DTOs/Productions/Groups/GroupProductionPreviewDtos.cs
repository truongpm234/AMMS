using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class GroupProductionScheduleStageDto
    {
        public string dept_code { get; set; } = "";

        public string dept_name { get; set; } = "";

        public string stage_type { get; set; } = "";

        public List<string> process_codes { get; set; } = new();

        public List<int> order_ids { get; set; } = new();

        public int? group_prod_id { get; set; }

        public int? split_prod_id { get; set; }

        public DateTime planned_start_date { get; set; }

        public DateTime planned_end_date { get; set; }

        public int duration_days { get; set; }

        public string note { get; set; } = "";
    }

    public class GroupProductionConfirmPreviewResponse
    {
        public List<int> order_ids { get; set; } = new();

        public List<string> process_codes { get; set; } = new();

        public List<string> selected_process_codes { get; set; } = new();

        public DateTime common_delivery_deadline { get; set; }

        public DateTime suggested_planned_start_date { get; set; }

        public DateTime estimated_finish_date { get; set; }

        public int total_duration_days { get; set; }

        public GroupProductionScheduleStageDto? dept1_private_stage { get; set; }

        public List<GroupProductionScheduleStageDto> private_stages { get; set; } = new();

        public List<GroupProductionScheduleStageDto> group_stages { get; set; } = new();

        public List<GroupProductionScheduleStageDto> split_stages { get; set; } = new();

        public List<GroupProductionScheduleStageDto> timeline { get; set; } = new();

        public bool can_meet_common_deadline { get; set; }

        public int days_late_if_any { get; set; }

        public List<GroupProductionPlanWarningDto> warnings { get; set; } = new();

        public List<string> notes { get; set; } = new();
    }
}
