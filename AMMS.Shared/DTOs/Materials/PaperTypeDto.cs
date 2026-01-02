namespace AMMS.Shared.DTOs.Materials
{
    public class PaperTypeDto
    {
        public string Code { get; set; } = null!;
        public string Name { get; set; } = null!;
        public decimal? StockQty { get; set; }
    }
}
