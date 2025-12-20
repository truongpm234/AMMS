using AMMS.Infrastructure.Entities;

namespace AMMS.Application.Interfaces
{
    public interface IOrderService
    {
        Task<order> GetOrderByCodeAsync(string code);
    }
}