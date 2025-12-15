namespace AMMS.Shared.DTOs.Orders
{
    public class CreateCustomerOrderRequest
    {
        // CUSTOMER
        public string CustomerName { get; set; } = null!;
        public string CustomerPhone { get; set; } = null!;
        public string? CustomerEmail { get; set; }

        // ORDER
        public DateTime? DeliveryDate { get; set; }

        // ORDER ITEM
        public string ProductName { get; set; } = null!;
        public int Quantity { get; set; }

        // UI extra
        public string? Description { get; set; }

        public string? DesignFilePath { get; set; }
    }
}
