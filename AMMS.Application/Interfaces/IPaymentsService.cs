using AMMS.Infrastructure.Entities;

namespace AMMS.Application.Interfaces
{
    public interface IPaymentsService
    {
        Task<payment?> GetPaidByProviderOrderCodeAsync(string provider, long orderCode, CancellationToken ct = default);
    }
}