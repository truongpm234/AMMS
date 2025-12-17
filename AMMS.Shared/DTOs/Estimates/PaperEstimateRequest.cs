namespace AMMS.Shared.DTOs.Estimates
{
    public class PaperEstimateRequest
    {
        public string paper_code { get; set; } = null!;
        public int quantity { get; set; }

        // kích thước sản phẩm (mm) - từ UI
        public int length_mm { get; set; }  // Dài
        public int width_mm { get; set; }   // Rộng
        public int height_mm { get; set; }  // Cao

        // tuỳ chỉnh
        public int allowance_mm { get; set; } = 10; // chừa gấp/dán
        public int bleed_mm { get; set; } = 3;      // chừa xén
    }
}
