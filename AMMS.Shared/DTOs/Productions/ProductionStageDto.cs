using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public sealed class ProductionStageDto
    {
        // Step definition
        public int? process_id { get; set; }
        public int seq_num { get; set; }
        public string process_name { get; set; } = "";
        public string? process_code { get; set; }
        public string? machine { get; set; }

        // Task state
        public int? task_id { get; set; }
        public string? task_name { get; set; }
        public string? status { get; set; }
        public int? assigned_to { get; set; }
        public string? assigned_to_name { get; set; }
        public DateTime? start_time { get; set; }
        public DateTime? end_time { get; set; }

        // Sản lượng/hao phí từ logs
        public int qty_good { get; set; }
        public int qty_bad { get; set; }
        public decimal waste_percent { get; set; }
        public DateTime? last_scan_time { get; set; }

        public List<TaskLogDto> logs { get; set; } = new();
        public List<StageMaterialDto> input_materials { get; set; } = new();
        public StageMaterialDto? output_product { get; set; }
    }
}
