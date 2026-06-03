using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class CreateTaskQrFormRequest
    {
        public int task_id { get; set; }

        public int ttl_minutes { get; set; } = 6000;

        public int? qty_good { get; set; }

        public bool use_manual_input { get; set; } = false;

        public string? reason { get; set; }

        public List<IFormFile>? images { get; set; } = new();

        public string? materials_json { get; set; }

        public string? reference_inputs_json { get; set; }

        public string? outputs_json { get; set; }

    }

    public class TaskSubProductLeftoverInputDto
    {
        public string? process_code { get; set; }

        public string? process_name { get; set; }

        public string? unit { get; set; } = "sp";

        public decimal quantity_left { get; set; }

        public string? note { get; set; }
    }

}