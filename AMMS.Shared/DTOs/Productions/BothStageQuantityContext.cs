using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Productions
{
    public sealed class BothStageQuantityContext
    {
        public int stage_sheet_qty { get; init; }
        public int stage_output_qty { get; init; }
        public bool is_both { get; init; }
        public bool is_stage_covered_by_sub { get; init; }
        public decimal nvl_ratio { get; init; }
    }
}
