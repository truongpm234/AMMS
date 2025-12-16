using AMMS.Domain.Entities;

namespace AMMS.Domain
{
    public interface IOrderRepository
    {
        Task AddAsync(order_request entity);
        void Update(order_request entity);
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
    }
}
