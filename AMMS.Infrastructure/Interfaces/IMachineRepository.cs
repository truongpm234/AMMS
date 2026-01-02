using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Estimates;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Interfaces
{
    public interface IMachineRepository
    {
        // --- existing ---
        Task<List<FreeMachineDto>> GetFreeMachinesAsync();
        Task<int> CountAllAsync();
        Task<int> CountActiveAsync();
        Task<int> CountRunningAsync();
        Task<List<machine>> GetActiveMachinesAsync();
        Task<List<machine>> GetMachinesByProcessAsync(string processName);
        Task<Dictionary<string, decimal>> GetDailyCapacityByProcessAsync();
        Task<machine?> GetByMachineCodeAsync(string machineCode);
        Task<machine?> FindFirstActiveByProcessNameAsync(string processName);

        // ⚠️ FIX signature: không để async Task<machine>? (sai)
        Task<machine?> FindMachineByProcess(string processName);

        // --- NEW for busy/free update ---
        Task<machine?> GetByMachineCodeForUpdateAsync(string machineCode, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);

        // optional helper
        Task AllocateAsync(string machineCode, int need = 1, CancellationToken ct = default);
        Task ReleaseAsync(string machineCode, int release = 1, CancellationToken ct = default);
    }
}
