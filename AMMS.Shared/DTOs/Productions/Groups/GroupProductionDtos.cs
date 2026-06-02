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
        public string suggestion_type { get; set; } = "GROUP";

        public List<int> suggest_order { get; set; } = new();

        public List<string> suggest_process { get; set; } = new();

        public string? department_code { get; set; }

        public string? department_name { get; set; }

        public string? material_key { get; set; }

        public string? reason { get; set; }

        public int? product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public string? production_method { get; set; }

        public string? note { get; set; }

        public DateTime? suggested_planned_start_date { get; set; }

        public DateTime? common_delivery_deadline { get; set; }

        public DateTime? estimated_finish_date { get; set; }

        public int estimated_total_days { get; set; } = 7;

        public string? suggestion_key { get; set; }

        public bool can_group { get; set; }

        public bool create_group_allowed { get; set; }

        public int order_count { get; set; }

        public List<string?> order_codes { get; set; } = new();

        public List<SuggestionOrderPreviewDto> orders { get; set; } = new();

        public List<SuggestionBatchPreviewDto> batches { get; set; } = new();

        public DateTime? schedule_planned_start_date { get; set; }

        public DateTime? schedule_planned_end_date { get; set; }

        public GroupProductionConfirmPreviewResponse? preview { get; set; }

        public List<SuggestedSplitProductionDto> auto_split_productions { get; set; } = new();

        public List<GroupProductionPlanWarningDto> warnings { get; set; } = new();
    }

    public class SuggestedSplitProductionDto
    {
        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int single_prod_id { get; set; }

        public string department_code { get; set; } = "DEPT_3";

        public string department_name { get; set; } = "Bế - Dứt - Dán";

        public List<string> process_codes { get; set; } = new();

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
