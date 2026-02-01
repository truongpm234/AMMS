using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.ProductTemplates
{
    public class PaperStockDto
    {
        public int material_id { get; set; }
        public string code { get; set; } = "";
        public string name { get; set; } = "";
        public string unit { get; set; } = "";
        public decimal stock_qty { get; set; }
        public decimal? cost_price { get; set; }
    }
}

