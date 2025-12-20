using AMMS.Infrastructure.Entities;
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
        Task<List<string>> GetAllTypeFormAsync();
    }
}
