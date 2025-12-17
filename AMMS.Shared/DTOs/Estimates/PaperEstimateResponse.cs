namespace AMMS.Shared.DTOs.Estimates
{
    public class PaperEstimateResponse
    {
        public string paper_code { get; set; } = null!;

        // Khổ giấy
        public int sheet_width_mm { get; set; }
        public int sheet_height_mm { get; set; }

        // Kích thước in (1 sản phẩm)
        public int print_width_mm { get; set; }
        public int print_height_mm { get; set; }

        // 1 tờ in được bao nhiêu sản phẩm
        public int n_up { get; set; }

        // SL sản phẩm
        public int quantity { get; set; }

        // Số tờ giấy
        public int sheets_base { get; set; }
        public int sheets_with_waste { get; set; }

        // (tuỳ chọn) hao hụt để FE hiển thị rõ
        public decimal waste_percent { get; set; }
    }
}
