using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.SubProduct
{
    public class SubProductImportReceiptResponseDto
    {
        public bool success { get; set; }
        public int sub_product_id { get; set; }
        public string? import_file { get; set; }
        public string message { get; set; } = "";
    }

    public class ImportPendingSubProductsResponseDto
    {
        public bool success { get; set; }
        public int total_pending { get; set; }
        public int merged_count { get; set; }
        public int activated_count { get; set; }
        public List<ImportPendingSubProductRowDto> rows { get; set; } = new();
        public string message { get; set; } = "";
    }

    public class ImportPendingSubProductRowDto
    {
        public int pending_sub_product_id { get; set; }
        public int? merged_into_sub_product_id { get; set; }
        public string action { get; set; } = "";
        public int quantity { get; set; }
        public int product_type_id { get; set; }
        public int? width { get; set; }
        public int? length { get; set; }
        public string? product_process { get; set; }
    }
}
