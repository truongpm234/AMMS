using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Estimates
{
    public class WasteResult
    {
        public int WastePrinting { get; set; }
        public int WasteDieCutting { get; set; }
        public int WasteMounting { get; set; }
        public int WasteCoating { get; set; }
        public int WasteLamination { get; set; }
        public int WasteGluing { get; set; }
        public int TotalWaste { get; set; }
        public decimal WastePercent { get; set; }
    }
}
