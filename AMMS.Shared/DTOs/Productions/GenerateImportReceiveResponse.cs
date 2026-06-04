namespace AMMS.Shared.DTOs.Productions
{
    public class GenerateImportReceiveResponse
    {
        public bool success { get; set; }
        public int prod_id { get; set; }
        public List<int> prod_ids { get; set; } = new();
        public int order_id { get; set; }
        public string order_code { get; set; } = string.Empty;
        public string import_recieve_path { get; set; } = string.Empty;
        public int total_productions_in_file { get; set; }
        public string message { get; set; } = string.Empty;
    }
}