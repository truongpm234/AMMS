using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IMissingMaterialRepository
    {
        Task<PagedResultLite<MissingMaterialDto>> GetPagedFromDbAsync(int page, int pageSize, CancellationToken ct = default);
        Task<object> RecalculateAndSaveAsync(CancellationToken ct = default);
        Task<List<MissingMaterialPurchasePdfRowDto>> GetPurchasePdfRowsAsync(int page, int pageSize, CancellationToken ct = default);
        Task<int> UpdateFilePurposeAsync(string filePurpose, CancellationToken ct = default);
        Task<List<MissingMaterialPurchasePdfRowDto>> GetPurchasePdfRowsByMissIdsAsync(List<long> missIds, CancellationToken ct = default);
        Task<int> UpdateFilePurposeByMissIdsAsync(List<long> missIds, string filePurpose, CancellationToken ct = default);
    }
}
