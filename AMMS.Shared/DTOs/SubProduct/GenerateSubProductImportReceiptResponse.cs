using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.SubProduct
{
    public class GenerateSubProductImportReceiptResponse
    {
        public bool success { get; set; }

        public string? import_file { get; set; }

        public List<int> sub_product_ids { get; set; } = new();

        public string? message { get; set; }
    }
}