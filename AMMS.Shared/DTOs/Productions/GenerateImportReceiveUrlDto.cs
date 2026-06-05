namespace AMMS.Shared.DTOs.Productions
{
    public class GenerateImportReceiveUrlResponse
    {
        public bool success { get; set; }

        public int order_id { get; set; }

        public string? order_code { get; set; }

        public int total_productions { get; set; }

        public int generated_count { get; set; }

        public List<int> prod_ids { get; set; } = new();

        /*
         * FE dùng field này để mở file PDF Cloudinary.
         */
        public string file_url { get; set; } = "";

        /*
         * Giữ lại tên cũ để không vỡ FE cũ.
         */
        public string import_recieve_path { get; set; } = "";

        /*
         * Alias dễ hiểu hơn.
         */
        public string import_file { get; set; } = "";

        public List<GenerateImportReceiveUrlFileItem> files { get; set; } = new();

        public string? message { get; set; }
    }

    public class GenerateImportReceiveUrlFileItem
    {
        public bool success { get; set; }

        public int prod_id { get; set; }

        public List<int> prod_ids { get; set; } = new();

        public int order_id { get; set; }

        public string? order_code { get; set; }

        public string file_url { get; set; } = "";

        public string import_recieve_path { get; set; } = "";

        public string import_file { get; set; } = "";

        public int total_productions_in_file { get; set; }

        public string? message { get; set; }
    }
}