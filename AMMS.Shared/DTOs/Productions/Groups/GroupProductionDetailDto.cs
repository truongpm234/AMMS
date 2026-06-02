using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions.Groups
{
    public class GroupProductionDetailDto
    {
        public int prod_id { get; set; }
        public string? code { get; set; }
        public string? status { get; set; }
        public DateTime? planned_end_date { get; set; }
        public int? product_type_id { get; set; }
        public string? product_type_name { get; set; }
        public int total_qty { get; set; }
        public string? process_codes { get; set; }
        public bool can_start { get; set; }
        public DateTime? planned_start_date { get; set; }
        public DateTime? actual_start_date { get; set; }
        public DateTime? end_date { get; set; }
        public string? issue_file { get; set; }
        public string? can_start_message { get; set; }
        public GroupProductionTaskContextDto? task_context { get; set; }
        public List<GroupProductionOrderDto> orders { get; set; } = new();
        public List<GroupProductionStageDto> stages { get; set; } = new();
        public GroupProductionConfirmPreviewResponse? preview { get; set; }
        public GroupProductionPreviousStageContextDto? previous_stage_context { get; set; }

    }

    public class GroupProductionOrderDto
    {
        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int single_prod_id { get; set; }

        public int qty { get; set; }

        public string? status { get; set; }
    }

    public class GroupProductionStageDto
    {
        public int task_id { get; set; }

        public int? seq_num { get; set; }

        public string? process_code { get; set; }

        public string? process_name { get; set; }

        public string? status { get; set; }

        public DateTime? start_time { get; set; }

        public DateTime? end_time { get; set; }

        public decimal estimated_output_qty { get; set; }

        public decimal actual_output_qty { get; set; }

        public List<string> report_image_urls { get; set; } = new();

        public List<GroupStageMaterialDto> input_materials { get; set; } = new();

        public List<GroupStageMaterialDto> outputs { get; set; } = new();

        public List<GroupTaskLogDto> logs { get; set; } = new();

        public List<GroupTaskAllocationDto> allocations { get; set; } = new();
    }

    public class GroupStageMaterialDto
    {
        public string? code { get; set; }

        public string? name { get; set; }

        public string? unit { get; set; }

        public decimal estimated_qty { get; set; }

        public decimal actual_qty { get; set; }
    }

    public class GroupTaskLogDto
    {
        public int log_id { get; set; }

        public int task_id { get; set; }

        public string? action_type { get; set; }

        public int qty_good { get; set; }

        public DateTime? log_time { get; set; }

        public string? reason { get; set; }

        public string? reference_input_json { get; set; }

        public string? material_usage_json { get; set; }

        public string? output_json { get; set; }
        public string? report_image_url { get; set; }

        public List<string> report_image_urls { get; set; } = new();
    }

    public class GroupTaskAllocationDto
    {
        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int? single_task_id { get; set; }

        public int qty_good { get; set; }

        public string? output_json { get; set; }
    }
}
