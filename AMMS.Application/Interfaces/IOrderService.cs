using AMMS.Infrastructure.Entities;

namespace AMMS.Application.Interfaces
{
    public interface IOrderService
    {
        Task<order> GetOrderByCodeAsync(string code);
        Task<order> GetByIdAsync(int id);
        Task<List<order>> GetAllAsync();
    }
}