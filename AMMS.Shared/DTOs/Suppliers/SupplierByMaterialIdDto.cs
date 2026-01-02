
namespace AMMS.Shared.DTOs.Suppliers
{
    public class SupplierByMaterialIdDto
    {
        public int SupplierId { get; set; }
        public string Name { get; set; } = null!;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public decimal? Rating { get; set; }

        public decimal? Price { get; set; }
    }
}
