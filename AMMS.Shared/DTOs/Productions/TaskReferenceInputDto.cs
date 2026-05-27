using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public class TaskReferenceInputDto
    {
        public string input_code { get; set; } = "";
        public string input_name { get; set; } = "";
        public string unit { get; set; } = "";
        public decimal estimated_qty { get; set; }
        public decimal actual_qty_prev_stage { get; set; }
    }
}