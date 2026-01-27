using AMMS.Infrastructure.Entities;

namespace AMMS.Application.Interfaces
{
    public interface IPaymentsService
    {
        Task<payment?> GetPaidByProviderOrderCodeAsync(string provider, long orderCode, CancellationToken ct = default);
        Task<payment?> GetLatestByRequestIdAsync(int orderRequestId, CancellationToken ct = default);
        Task<payment?> GetLatestPendingByRequestIdAsync(int requestId, CancellationToken ct);

        Task UpsertPendingAsync(payment p, CancellationToken ct);
        Task<int> SaveChangesAsync(CancellationToken ct = default);
    }
}