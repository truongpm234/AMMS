using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    // Chi tiết thời gian cho 1 công đoạn
    public class ProcessTimeDetail
    {
        public string ProcessName { get; set; } = string.Empty;
        public int RequiredQuantity { get; set; }
        public decimal TotalDailyCapacity { get; set; }
        public double DaysNeeded { get; set; }
        public int MachineCount { get; set; }
        public decimal CapacityPerHour { get; set; }
        public bool IsBottleneck { get; set; }
    }
}
