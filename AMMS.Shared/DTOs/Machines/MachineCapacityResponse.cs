using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Machines
{
    public class MachineCapacityResponse
    {
        public int TotalMachines { get; set; }        // Tổng máy đang có
        public int ActiveMachines { get; set; }       // Tổng máy đang hoạt động (is_active = true)
        public int RunningMachines { get; set; }      // Tổng máy đang chạy (distinct theo machine_code)
    }
}
