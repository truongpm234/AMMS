using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class PaymentsService : IPaymentsService
    {
        private readonly IPaymentRepository _paymentRepository;
        public PaymentsService(IPaymentRepository paymentRepository)
        {
            _paymentRepository = paymentRepository;
        }

        public Task<payment?> GetPaidByProviderOrderCodeAsync(string provider, long orderCode, CancellationToken ct = default)
        {
            return _paymentRepository.GetPaidByProviderOrderCodeAsync(provider, orderCode, ct);
        }
        public Task<payment?> GetLatestByRequestIdAsync(int orderRequestId, CancellationToken ct = default)
        {
            return _paymentRepository.GetLatestByRequestIdAsync(orderRequestId, ct);
        }
        public async Task<payment?> GetLatestPendingByRequestIdAsync(int requestId, CancellationToken ct)
        {
            return await _paymentRepository.GetLatestPendingByRequestIdAsync(requestId, ct);
        }
        public async Task UpsertPendingAsync(payment p, CancellationToken ct)
        {
            await _paymentRepository.UpsertPendingAsync(p, ct);
        }
         public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            return _paymentRepository.SaveChangesAsync(ct);
        }
    }
}
