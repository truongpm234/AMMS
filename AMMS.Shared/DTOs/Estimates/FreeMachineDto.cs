using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class FreeMachineDto
    {
        public string ProcessName { get; set; } = null!;
        public int TotalMachines { get; set; }
        public int BusyMachines { get; set; }
        public int FreeMachines { get; set; }
    }
}
