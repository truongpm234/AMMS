using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.ProductTemplates
{
    public class ProductTemplatesWithPaperStockResponse
    {
        public List<PaperStockDto> paper_stock { get; set; } = new();
    }
}
