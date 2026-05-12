using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskLogDto
    {
        public int log_id { get; set; }
        public int task_id { get; set; }
        public string? action_type { get; set; }
        public int qty_good { get; set; }
        public DateTime? log_time { get; set; }
        public string? scanned_code { get; set; }
        public int? scanned_by_user_id { get; set; }
        public string? reason { get; set; }
        public string? comment { get; set; }
        public string? report_image_url { get; set; }
        public List<string> report_image_urls { get; set; } = new();
        [JsonIgnore]
        public string? material_usage_json { get; set; }

        public List<TaskMaterialUsageLogItemDto>? material_usages { get; set; }
    }
}
