using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskQrTokenPayloadDto
    {
        public int task_id { get; set; }

        public string? name { get; set; }

        public int qty_good { get; set; }

        public int? prod_id { get; set; }

        public long exp_unix { get; set; }

        public bool use_manual_input { get; set; } = false;

        public string? reason { get; set; }

        public string? report_image_url { get; set; }

        public List<TaskMaterialUsageInputDto> materials { get; set; } = new();

        public List<TaskReferenceUsageInputDto> reference_inputs { get; set; } = new();

        public List<TaskOutputReportDto> outputs { get; set; } = new();
    }
}

