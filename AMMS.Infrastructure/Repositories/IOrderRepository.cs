using AMMS.Infrastructure.Entities;

namespace AMMS.Infrastructure.Repositories
{
    public interface IOrderRepository
    {
        Task AddAsync(order entity);
        void Update(order entity);
        Task<order?> GetByIdAsync(int id);
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
    }
}