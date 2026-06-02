using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskQrMaterialBundleDto
    {
        public int task_id { get; set; }

        public int? prod_id { get; set; }

        public string? prod_kind { get; set; }

        public string? prod_method { get; set; }

        public DateTime? production_planned_start_date { get; set; }

        public DateTime? production_planned_end_date { get; set; }

        public DateTime? task_planned_start_time { get; set; }

        public DateTime? task_planned_end_time { get; set; }
        public List<TaskConsumableMaterialDto> consumable_materials { get; set; } = new();
        public List<TaskReferenceInputDto> reference_inputs { get; set; } = new();
    }
}