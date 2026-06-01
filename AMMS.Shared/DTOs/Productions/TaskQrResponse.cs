using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskQrResponse
    {
        public int task_id { get; set; }

        public string token { get; set; } = "";

        public long expires_at_unix { get; set; }

        public int qty_good_used { get; set; }

        public bool is_auto_filled { get; set; }

        public int min_allowed { get; set; }

        public int max_allowed { get; set; }

        public int suggested_qty { get; set; }

        public string? qty_unit { get; set; }

        public string? process_code { get; set; }

        public string? process_name { get; set; }

        public int embedded_material_count { get; set; }

        public List<TaskConsumableMaterialDto> consumable_materials { get; set; } = new();

        public List<TaskReferenceInputDto> reference_inputs { get; set; } = new();

        /*
         * NEW:
         * Link FE mở task detail.
         */
        public string? link { get; set; }

        /*
         * NEW:
         * Echo lại raw JSON mà FE nhập ở request form-data.
         * Field nào không gửi hoặc rỗng thì trả null.
         */
        public TaskQrRequestJsonEchoDto? request_json { get; set; }

        /*
         * Nếu bạn đang dùng submitted_payload cũ thì giữ lại.
         */
        public TaskQrSubmittedPayloadDto? submitted_payload { get; set; }
    }

    public class TaskQrRequestJsonEchoDto
    {
        public string? materials_json { get; set; }

        public string? reference_inputs_json { get; set; }

        public string? outputs_json { get; set; }

        public string? sub_product_leftovers_json { get; set; }
    }

    public class TaskQrSubmittedPayloadDto
    {
        public int task_id { get; set; }

        public int ttl_minutes { get; set; }

        public int? qty_good { get; set; }

        public bool use_manual_input { get; set; }

        public string? reason { get; set; }

        public string? report_image_url { get; set; }

        public List<string> image_urls { get; set; } = new();
        public List<TaskMaterialUsageInputDto> materials { get; set; } = new();
        public List<TaskReferenceUsageInputDto> reference_inputs { get; set; } = new();
        public List<TaskReferenceUsageInputDto> qr_reference_inputs { get; set; } = new();
        public List<TaskOutputReportDto> outputs { get; set; } = new();
        public List<TaskSubProductLeftoverInputDto> sub_product_leftovers { get; set; } = new();
        public TaskQrSubmittedRawJsonDto raw_json { get; set; } = new();
    }

    public class TaskQrSubmittedRawJsonDto
    {
        public string? materials_json { get; set; }

        public string? reference_inputs_json { get; set; }

        public string? outputs_json { get; set; }

        public string? sub_product_leftovers_json { get; set; }
    }
}
