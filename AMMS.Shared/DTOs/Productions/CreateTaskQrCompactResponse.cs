using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class CreateTaskQrCompactResponse
    {
        public string token { get; set; } = "";

        public string link { get; set; } = "";
    }

    public class DecodeTaskQrTokenRequest
    {
        public string? token { get; set; }
    }

    public class DecodeTaskQrTokenResponse
    {
        public bool valid { get; set; }

        public string token { get; set; } = "";

        public string link { get; set; } = "";

        public int task_id { get; set; }

        public int qty_good { get; set; }

        public long exp_unix { get; set; }

        public bool use_manual_input { get; set; }

        public string? reason { get; set; }

        public string? report_image_url { get; set; }

        /*
         * Dữ liệu thật sự đang nằm trong token để finish.
         */
        public List<TaskMaterialUsageInputDto> materials { get; set; } = new();

        public List<TaskReferenceUsageInputDto> qr_reference_inputs { get; set; } = new();

        public List<TaskOutputReportDto> outputs { get; set; } = new();

        /*
         * Dữ liệu request gốc đã nhập khi tạo QR.
         * Bao gồm:
         * - materials
         * - reference_inputs gốc
         * - qr_reference_inputs đã merge leftovers
         * - outputs
         * - sub_product_leftovers
         * - raw_json
         */
        public TaskQrSubmittedPayloadDto? submitted_payload { get; set; }
    }
}