using AMMS.Infrastructure.Entities;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IOrderRepository
    {
        Task AddOrderAsync(order entity);
        Task AddOrderItemAsync(order_item entity);
        void Update(order entity);
        Task<order?> GetByIdAsync(int id);
        Task<order?> GetByCodeAsync(string code);
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
        Task<string> GenerateNextOrderCodeAsync();
    }
}