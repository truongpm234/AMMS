using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Estimates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IMachineRepository
    {
        Task<List<FreeMachineDto>> GetFreeMachinesAsync();
        Task<int> CountAllAsync();
        Task<int> CountActiveAsync();
        Task<int> CountRunningAsync();
        Task<List<machine>> GetActiveMachinesAsync();
        Task<List<machine>> GetMachinesByProcessAsync(string processName);
        Task<Dictionary<string, decimal>> GetDailyCapacityByProcessAsync();
    }
}
