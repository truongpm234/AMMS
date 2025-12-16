using AMMS.Infrastructure.Entities;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IRequestRepository
    {
        Task AddAsync(order_request entity);
        void Update(order_request entity);
        Task<order_request?> GetByIdAsync(int id);
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
    }
}