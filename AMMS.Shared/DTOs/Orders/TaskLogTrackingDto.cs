using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class TaskLogTrackingDto
    {
        public int log_id { get; set; }
        public int? task_id { get; set; }
        public string? scanned_code { get; set; }
        public string? action_type { get; set; }
        public int? qty_good { get; set; }
        public DateTime? log_time { get; set; }
        public int? scanned_by_user_id { get; set; }
        public object? material_usage_json { get; set; }
        public string? reason { get; set; }
        public string? report_image_url { get; set; }
        public object? reference_input_json { get; set; }
        public object? output_json { get; set; }
    }
}
