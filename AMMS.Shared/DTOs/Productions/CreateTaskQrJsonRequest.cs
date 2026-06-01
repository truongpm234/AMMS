using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class CreateTaskQrJsonRequest
    {
        public int task_id { get; set; }

        public int ttl_minutes { get; set; } = 60;

        public int? qty_good { get; set; }

        public bool use_manual_input { get; set; } = false;

        public string? reason { get; set; }

        // Cho phép FE gửi dạng string JSON giống form-data cũ
        public string? materials_json { get; set; }

        public string? reference_inputs_json { get; set; }

        public string? outputs_json { get; set; }

        public string? sub_product_leftovers_json { get; set; }

        // Cho phép FE gửi dạng array JSON mới
        public List<TaskMaterialUsageInputDto>? materials { get; set; }

        public List<TaskReferenceUsageInputDto>? reference_inputs { get; set; }

        public List<TaskOutputReportDto>? outputs { get; set; }

        public List<TaskSubProductLeftoverInputDto>? sub_product_leftovers { get; set; }
    }
}
