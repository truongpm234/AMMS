using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class TaskByProcessDto
    {
        public int task_id { get; set; }
        public int? prod_id { get; set; }
        public string? name { get; set; }
        public int? seq_num { get; set; }
        public string? status { get; set; }
        public string? machine { get; set; }
        public DateTime? start_time { get; set; }
        public DateTime? end_time { get; set; }
        public int? process_id { get; set; }
        public DateTime? planned_start_time { get; set; }
        public DateTime? planned_end_time { get; set; }
        public string? reason { get; set; }
        public bool is_taken_sub_product { get; set; }
        public string? input_mode { get; set; }
        public bool is_current { get; set; }
    }
}
