using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Common;
using AMMS.Shared.DTOs.Materials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IMaterialService
    {
        Task<List<material>> GetAllAsync();
        Task<material?> GetByIdAsync(int id);
        Task UpdateAsync(material material);
        Task<List<string>> GetAllPaperTypeAsync();
        Task<PagedResultLite<MaterialShortageDto>> GetShortageForAllOrdersPagedAsync(
            int page, int pageSize, CancellationToken ct = default);     

    }
}
