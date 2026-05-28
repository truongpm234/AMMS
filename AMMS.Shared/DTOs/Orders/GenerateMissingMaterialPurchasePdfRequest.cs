using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.Orders
{
    public class GenerateMissingMaterialPurchasePdfRequest
    {
        public List<long> miss_ids { get; set; } = new();
    }
}
