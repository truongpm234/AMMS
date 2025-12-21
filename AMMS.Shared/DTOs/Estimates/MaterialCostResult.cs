using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class MaterialCostResult
    {
        public decimal InkCost { get; set; }
        public decimal InkWeightKg { get; set; }
        public decimal InkRate { get; set; }
        public decimal CoatingGlueCost { get; set; }
        public decimal CoatingGlueWeightKg { get; set; }
        public decimal CoatingGlueRate { get; set; }
        public decimal MountingGlueCost { get; set; }
        public decimal MountingGlueWeightKg { get; set; }
        public decimal MountingGlueRate { get; set; }
        public decimal LaminationCost { get; set; }
        public decimal LaminationWeightKg { get; set; }
        public decimal LaminationRate { get; set; }
    }
}
