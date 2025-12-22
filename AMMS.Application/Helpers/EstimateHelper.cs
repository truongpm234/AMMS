using AMMS.Shared.DTOs.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Helpers
{
    public class EstimateHelper
    {
        public static decimal GetQuantityForProcess(ProcessType p, int sheetsWithWaste, int productQuantity, decimal totalPrintAreaM2)
        {
            return p switch
            {
                ProcessType.IN => totalPrintAreaM2,
                ProcessType.PHU => totalPrintAreaM2,
                ProcessType.CAN_MANG => totalPrintAreaM2,

                ProcessType.BE => sheetsWithWaste,
                ProcessType.BOI => sheetsWithWaste,
                ProcessType.RALO => sheetsWithWaste,

                ProcessType.DAN => productQuantity,
                ProcessType.DOT => productQuantity,

                ProcessType.DUT => 0,
                ProcessType.CAT => 0,
                _ => 0
            };
        }
        public static bool IsProcessApplied(ProcessType p, List<ProcessType> selected, CoatingType coatingType, bool hasLamination)
        {

            if (p == ProcessType.IN) return true;

            if (p == ProcessType.PHU)
                return selected.Contains(ProcessType.PHU);

            if (p == ProcessType.CAN_MANG)
                return selected.Contains(ProcessType.CAN_MANG) || hasLamination;

            // DUT, CAT không tính
            if (p == ProcessType.DUT || p == ProcessType.CAT)
                return selected.Contains(p);

            return selected.Contains(p);
        }
    }
}
