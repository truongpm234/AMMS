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
        Task<object> RecalculateAndSaveAsync(CancellationToken ct = default);

        Task<PagedResultLite<MissingMaterialDto>> GetPagedFromDbAsync(
            int page,
            int pageSize,
            CancellationToken ct = default);
    }
}
