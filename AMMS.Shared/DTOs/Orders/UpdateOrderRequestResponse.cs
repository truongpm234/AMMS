namespace AMMS.Shared.DTOs.Orders
{
    public class UpdateOrderRequestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int UpdatedId { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}