using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;

namespace AMMS.Application.Services
{
    public class MissingMaterialService : IMissingMaterialService
    {
        private readonly IMissingMaterialRepository _repo;

        public MissingMaterialService(IMissingMaterialRepository repo)
        {
            _repo = repo;
        }

        public async Task<PagedResultLite<MissingMaterialDto>> GetPagedAsync(
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            await _repo.RecalculateAndSaveAsync(ct);

            var result = await _repo.GetPagedFromDbAsync(page, pageSize, ct);
            if (result.Data == null || result.Data.Count == 0)
                return result;

            static decimal RoundUpToHundreds(decimal value)
            {
                if (value <= 0m)
                    return 0m;

                return Math.Ceiling(value / 100m) * 100m;
            }

            foreach (var x in result.Data)
            {
                var baseQty = x.quantity;

                if (baseQty < 0m)
                    baseQty = 0m;

                var roundedQty = RoundUpToHundreds(baseQty);

                decimal unitPrice = 0m;

                if (baseQty > 0m && x.total_price > 0m)
                    unitPrice = x.total_price / baseQty;

                x.quantity = roundedQty;
                x.total_price = Math.Round(roundedQty * unitPrice, 2);
            }

            return result;
        }
    }
}