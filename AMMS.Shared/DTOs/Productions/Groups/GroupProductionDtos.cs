using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class SuggestGroupProductionRequest
    {
        public int? product_type_id { get; set; }
    }

    public class SuggestedGroupProductionDto
    {
        public List<int> suggest_order { get; set; } = new();

        public List<string> suggest_process { get; set; } = new();

        public string? department_code { get; set; }

        public string? department_name { get; set; }

        public string? material_key { get; set; }

        public string? reason { get; set; }
    }

    public class GroupProductionPreviousStageContextDto
    {
        public int? current_group_task_id { get; set; }

        public int? current_group_prod_id { get; set; }

        public string? current_process_code { get; set; }

        public string? current_process_name { get; set; }

        public string? previous_process_code { get; set; }

        public bool all_previous_finished { get; set; }

        public List<GroupProductionPreviousTaskByOrderDto> previous_tasks { get; set; } = new();
    }

    public class GroupProductionPreviousTaskByOrderDto
    {
        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int? previous_task_id { get; set; }

        public int? previous_prod_id { get; set; }

        public string? previous_prod_kind { get; set; }

        public int? previous_seq_num { get; set; }

        public string? previous_process_code { get; set; }

        public string? previous_process_name { get; set; }

        public string? previous_task_status { get; set; }

        public DateTime? previous_start_time { get; set; }

        public DateTime? previous_end_time { get; set; }

        public bool is_finished { get; set; }

        public string? message { get; set; }
    }

    public class GroupProductionPlanWarningDto
    {
        public string process_code { get; set; } = "";

        public string reason { get; set; } = "";

        public List<int> affected_order_ids { get; set; } = new();

        public Dictionary<string, List<int>> material_groups { get; set; } = new();
    }

    public class TaskPreviousInfoDto
    {
        public int? task_id { get; set; }

        public int? prod_id { get; set; }

        public int? seq_num { get; set; }

        public string? process_code { get; set; }

        public string? process_name { get; set; }

        public string? status { get; set; }

        public DateTime? start_time { get; set; }

        public DateTime? end_time { get; set; }
    }

    public class GroupProductionTaskContextDto
    {
        public int task_id { get; set; }

        public int? prod_id { get; set; }

        public string? prod_kind { get; set; }

        public string? process_code { get; set; }

        public string? process_name { get; set; }

        public string? status { get; set; }

        public TaskPreviousInfoDto? previous_task { get; set; }
    }
}
