using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public sealed class TaskRow
    {
        public int ProdId { get; set; }
        public int? SeqNum { get; set; }
        public string ProcessName { get; set; } = "";
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }
}
