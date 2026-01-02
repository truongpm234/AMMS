namespace AMMS.Shared.DTOs.Materials
{
    public class MaterialTypePaperDto
    {
        public List<PaperTypeDto> PaperTypes { get; set; } = new();

        public string MostStockPaperNames { get; set; } = null!;
    }
}
