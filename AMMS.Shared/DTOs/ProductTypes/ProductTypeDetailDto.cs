using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.ProductTypes
{
    public class ProductTypeDetailDto
    {
        public int product_type_id { get; set; }
        public string code { get; set; } = "";
        public string name { get; set; } = "";
        public string? description { get; set; }

        public List<ProductTemplateDto> templates { get; set; } = new();
        public List<ProductTypeProcessDto> processes { get; set; } = new();
    }
}
