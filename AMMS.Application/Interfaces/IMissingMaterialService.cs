using AMMS.Application.Interfaces;
using AMMS.Application.Services;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace AMMS.Application.Interfaces
{
    public interface IMissingMaterialService
    {
        Task<object> RecalculateAndSaveAsync(CancellationToken ct = default);

        Task<PagedResultLite<MissingMaterialDto>> GetPagedAsync(
            int page,
            int pageSize,
            CancellationToken ct = default);
    }
}
