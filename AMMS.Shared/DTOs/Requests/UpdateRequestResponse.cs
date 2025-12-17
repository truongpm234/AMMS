namespace AMMS.Shared.DTOs.Requests
{
    public class UpdateRequestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int UpdatedId { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}