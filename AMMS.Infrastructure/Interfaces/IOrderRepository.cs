using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IOrderRepository
    {
        Task<List<OrderResponseDto>> GetPagedWithFulfillAsync(int skip, int take, CancellationToken ct = default);
        Task AddOrderAsync(order entity);
        Task AddOrderItemAsync(order_item entity);
        void Update(order entity);
        Task<order?> GetByIdForUpdateAsync(int orderId, CancellationToken ct = default);
        Task<order?> GetByIdAsync(int id);
        Task<order?> GetByCodeAsync(string code);
        Task<List<OrderListDto>> GetPagedAsync(int skip, int take);
        Task<int> CountAsync();
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
        Task<string> GenerateNextOrderCodeAsync();
        Task<OrderDetailDto?> GetDetailByIdAsync(int orderId, CancellationToken ct = default);
        Task<PagedResultLite<MissingMaterialDto>> GetAllMissingMaterialsAsync(int page, int pageSize, CancellationToken ct = default);
        Task<string> DeleteDesignFilePath(int orderRequestId);
        Task<object> BuyMaterialAndRecalcOrdersAsync(int materialId, decimal quantity, int managerUserId, CancellationToken ct = default);
        Task<List<order>> GetAllOrderInprocessStatus();
        Task MarkOrdersBuyByMaterialsAsync(List<int> materialIds, CancellationToken ct = default);
        Task MarkOrdersBuyByMaterialAsync(int materialId, CancellationToken ct = default);
        Task RecalculateIsEnoughForOrdersAsync(CancellationToken ct = default);
        Task<List<order_item>> GetOrderItemsByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<bool> IsOrderEnoughByOrderIdAsync(int orderId, CancellationToken ct = default);
        Task<OrderProductionTrackingRawResult> GetProductionTrackingByOrderStatusAsync(string status, int page, int pageSize, CancellationToken ct = default);
        Task<OrdersByProcessRawResult> GetOrdersByProcessCodeRawAsync(string? processCode, CancellationToken ct = default);
        Task<AllOrdersProductionTrackingRawResult> GetAllOrdersProductionTrackingRawAsync(int page, int pageSize, CancellationToken ct = default);
    }
    public class OrderProductionTrackingRawResult
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool HasNext { get; set; }

        public List<order> Orders { get; set; } = new();
        public List<OrderRequestLiteRaw> Requests { get; set; } = new();
        public List<production> Productions { get; set; } = new();
        public List<task> Tasks { get; set; } = new();
        public List<task_log> TaskLogs { get; set; } = new();
        public List<ProdOrderLiteRaw> ProdOrders { get; set; } = new();
    }

    public class OrderRequestLiteRaw
    {
        public int order_request_id { get; set; }
        public int? order_id { get; set; }
    }

    public class ProdOrderLiteRaw
    {
        public int order_id { get; set; }
        public int prod_id { get; set; }
        public int? single_prod_id { get; set; }
    }

    public class OrdersByProcessRawResult
    {
        public List<order> Orders { get; set; } = new();
        public List<production> Productions { get; set; } = new();
        public List<task> Tasks { get; set; } = new();
        public List<ProdOrderLiteRaw> ProdOrders { get; set; } = new();
        public List<TaskProcessLiteRaw> TaskProcesses { get; set; } = new();
    }

    public class TaskProcessLiteRaw
    {
        public int task_id { get; set; }
        public int? process_id { get; set; }
        public string? process_code { get; set; }
        public string? process_name { get; set; }
    }

    public class AllOrdersProductionTrackingRawResult
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool HasNext { get; set; }

        public List<order> Orders { get; set; } = new();
        public List<OrderRequestIdRaw> Requests { get; set; } = new();
        public List<production> Productions { get; set; } = new();
        public List<task> Tasks { get; set; } = new();
        public List<task_log> TaskLogs { get; set; } = new();
        public List<ProdOrderLiteRaw> ProdOrders { get; set; } = new();
        public List<TaskProcessLiteRaw> TaskProcesses { get; set; } = new();
    }

    public class OrderRequestIdRaw
    {
        public int order_request_id { get; set; }
        public int? order_id { get; set; }
    }
}