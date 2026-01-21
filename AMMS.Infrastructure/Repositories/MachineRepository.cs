using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class MachineRepository : IMachineRepository
    {
        private readonly AppDbContext _db;
        public MachineRepository(AppDbContext db) => _db = db;

        public async Task<List<FreeMachineDto>> GetFreeMachinesAsync()
        {
            var machines = await _db.machines
                .AsNoTracking()
                .Where(m => m.is_active)
                .ToListAsync();

            // Nếu DB đã có busy/free => dùng luôn
            var hasBusyFree = machines.Any(m => m.busy_quantity != null || m.free_quantity != null);

            if (hasBusyFree)
            {
                return machines
                    .GroupBy(m => m.process_name)
                    .Select(g =>
                    {
                        var totalQty = g.Sum(x => x.quantity);
                        var busyQty = g.Sum(x => x.busy_quantity ?? 0);
                        var freeQty = g.Sum(x => x.free_quantity ?? (x.quantity - (x.busy_quantity ?? 0)));

                        return new FreeMachineDto
                        {
                            ProcessName = g.Key,
                            TotalMachines = totalQty,
                            BusyMachines = busyQty,
                            FreeMachines = freeQty
                        };
                    })
                    .ToList();
            }

            // Fallback: tính từ tasks (nhưng phải theo status mới)
            var busyMachineCodes = await _db.tasks
                .AsNoTracking()
                .Where(t => t.machine != null &&
                            (t.status == "Ready" || t.status == "InProgress"))
                .Select(t => t.machine!)
                .ToListAsync();

            return machines
                .GroupBy(m => m.process_name)
                .Select(g =>
                {
                    var total = g.Sum(x => x.quantity);
                    var busy = g.Where(m => busyMachineCodes.Contains(m.machine_code))
                                .Sum(x => x.quantity);

                    return new FreeMachineDto
                    {
                        ProcessName = g.Key,
                        TotalMachines = total,
                        BusyMachines = busy,
                        FreeMachines = total - busy
                    };
                })
                .ToList();
        }

        public Task<int> CountAllAsync() => _db.machines.AsNoTracking().CountAsync();

        public Task<int> CountActiveAsync() => _db.machines.AsNoTracking().CountAsync(x => x.is_active);

        // ✅ status mới
        public Task<int> CountRunningAsync()
            => _db.tasks.AsNoTracking()
                .Where(t => (t.status == "Ready" || t.status == "InProgress")
                            && t.machine != null && t.machine != "")
                .Select(t => t.machine)
                .Distinct()
                .CountAsync();

        public Task<List<machine>> GetActiveMachinesAsync()
            => _db.machines.Where(m => m.is_active).ToListAsync();

        public Task<List<machine>> GetMachinesByProcessAsync(string processName)
            => _db.machines
                .Where(m => m.process_name == processName && m.is_active)
                .ToListAsync();

        public async Task<Dictionary<string, decimal>> GetDailyCapacityByProcessAsync()
        {
            var result = await _db.machines
                .Where(m => m.is_active)
                .GroupBy(m => m.process_name)
                .Select(g => new
                {
                    ProcessName = g.Key,
                    DailyCapacity = g.Sum(m =>
                        m.quantity * m.capacity_per_hour * m.working_hours_per_day * m.efficiency_percent / 100m
                    )
                })
                .ToDictionaryAsync(x => x.ProcessName, x => x.DailyCapacity);

            return result;
        }

        public Task<machine?> GetByMachineCodeAsync(string machineCode)
            => _db.machines.AsNoTracking()
                .FirstOrDefaultAsync(x => x.machine_code == machineCode && x.is_active);

        public Task<machine?> FindFirstActiveByProcessNameAsync(string processName)
            => _db.machines.AsNoTracking()
                .FirstOrDefaultAsync(x => x.is_active && x.process_name.ToLower() == processName.Trim().ToLower());

        public async Task<machine?> FindMachineByProcess(string processName)
        {
            return await _db.machines.AsNoTracking()
                .Where(m => m.is_active && m.process_name == processName)
                .OrderByDescending(m => m.capacity_per_hour)
                .FirstOrDefaultAsync();
        }

        public Task<machine?> GetByMachineCodeForUpdateAsync(string machineCode, CancellationToken ct = default)
            => _db.machines.FirstOrDefaultAsync(x => x.machine_code == machineCode && x.is_active, ct);

        public Task SaveChangesAsync(CancellationToken ct = default)
            => _db.SaveChangesAsync(ct);

        public async Task AllocateAsync(string machineCode, int need = 1, CancellationToken ct = default)
        {
            if (need <= 0) need = 1;

            var m = await GetByMachineCodeForUpdateAsync(machineCode, ct)
                ?? throw new Exception($"Machine '{machineCode}' not found");

            m.busy_quantity ??= 0;
            m.free_quantity ??= (m.quantity - m.busy_quantity.Value);

            if (m.free_quantity < need)
                throw new Exception($"Not enough free machines for '{machineCode}'. Free={m.free_quantity}, Need={need}");

            m.free_quantity -= need;
            m.busy_quantity += need;

            await _db.SaveChangesAsync(ct);
        }

        public async Task ReleaseAsync(string machineCode, int release = 1, CancellationToken ct = default)
        {
            if (release <= 0) release = 1;

            var m = await GetByMachineCodeForUpdateAsync(machineCode, ct)
                ?? throw new Exception($"Machine '{machineCode}' not found");

            m.busy_quantity ??= 0;
            m.free_quantity ??= (m.quantity - m.busy_quantity.Value);

            var realRelease = Math.Min(release, m.busy_quantity.Value);

            m.busy_quantity -= realRelease;
            m.free_quantity += realRelease;

            if (m.free_quantity > m.quantity) m.free_quantity = m.quantity;

            await _db.SaveChangesAsync(ct);
        }

        public async Task<List<machine>> GetAllAsync()
        {
            return await _db.machines.ToListAsync();
        }
    }
}
