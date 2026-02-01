using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class MissingMaterialService : IMissingMaterialService
    {
        private readonly IMissingMaterialRepository _repo;

        public MissingMaterialService(IMissingMaterialRepository repo)
        {
            _repo = repo;
        }

        public Task<object> RecalculateAndSaveAsync(CancellationToken ct = default)
            => _repo.RecalculateAndSaveAsync(ct);

        public async Task<PagedResultLite<MissingMaterialDto>> GetPagedAsync(
    int page,
    int pageSize,
    CancellationToken ct = default)
        {
            var result = await _repo.GetPagedFromDbAsync(page, pageSize, ct);
            if (result.Data == null || result.Data.Count == 0) return result;

            static decimal RoundUpToTens(decimal value)
            {
                if (value <= 0m) return 0m;
                return Math.Ceiling(value / 10m) * 10m;
            }

            foreach (var x in result.Data)
            {
                // quantity trong DB = remaining base
                var baseQty = x.quantity;
                if (baseQty < 0m) baseQty = 0m;

                // ✅ +10%
                var withBuffer = baseQty * 1.10m;

                // ✅ round lên bội 10
                var roundedQty = RoundUpToTens(withBuffer);

                // ✅ giữ unit price ngầm bằng total_price/baseQty
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
