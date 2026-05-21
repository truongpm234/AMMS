using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Shared.DTOs.SubProduct
{
    public class GenerateSubProductImportReceiptsRequestDto
    {
        public List<int> sub_product_ids { get; set; } = new();
    }

    public class SubProductImportReceiptBatchResponseDto
    {
        public bool success { get; set; }

        public string? import_file { get; set; }

        public string file_name { get; set; } = "";

        public int total_selected { get; set; }

        public List<SubProductImportReceiptItemDto> items { get; set; } = new();

        public string message { get; set; } = "";
    }

    public class SubProductImportReceiptItemDto
    {
        public int sub_product_id { get; set; }

        public int product_type_id { get; set; }

        public string? product_type_name { get; set; }

        public int? width { get; set; }

        public int? length { get; set; }

        public string? product_process { get; set; }

        public int quantity { get; set; }

        public bool is_active { get; set; }

        public bool is_imported { get; set; }

        public string? import_file { get; set; }
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