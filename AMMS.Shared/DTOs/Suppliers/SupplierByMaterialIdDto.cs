
namespace AMMS.Shared.DTOs.Suppliers
{
    public class SupplierByMaterialIdDto
    {
        public int supplier_id { get; set; }
        public string name { get; set; } = null!;
        public string? email { get; set; }
        public string? phone { get; set; }
        public decimal? rating { get; set; }
        public decimal? price { get; set; }
        public string? contact_person { get; set; }

    }
}
