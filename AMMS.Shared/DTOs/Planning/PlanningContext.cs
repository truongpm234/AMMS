using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Planning
{
    public sealed class PlanningContext
    {
        public int? OrderId { get; init; }
        public int OrderRequestId { get; init; }
        public int ProductTypeId { get; init; }
        public int OrderQty { get; init; }
        public int SheetsTotal { get; init; }
        public int SheetsRequired { get; init; }
        public int NUp { get; init; }
        public int NumberOfPlates { get; init; }
        public bool IsOneSideBox { get; init; }
        public int? LengthMm { get; init; }
        public int? WidthMm { get; init; }
        public int? HeightMm { get; init; }
        public decimal TotalAreaM2 { get; init; }
        public string? RawProductionProcessCsv { get; init; }
        public DateTime? DesiredDeliveryDate { get; init; }
        public DateTime QueueDateTime { get; init; }
        public int QueueOrderKey { get; init; }
        public int WaveSheetsRequired { get; set; }
        public int WaveSheetsUsed { get; set; }
        public string? ProductionMethod { get; init; }
        public string? SubProductProcess { get; init; }
        public int SubProductUsedQty { get; init; }
        public int NvlQty { get; init; }
    }
}
