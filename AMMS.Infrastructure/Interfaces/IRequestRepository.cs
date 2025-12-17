using AMMS.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IRequestRepository
    {
        DbContext DbContext { get; }
        Task AddAsync(order_request entity);
        Task UpdateAsync(order_request entity);
        Task<order_request?> GetByIdAsync(int id);
        Task DeleteAsync(int id);
        Task<int> SaveChangesAsync();
    }
}