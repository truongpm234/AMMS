using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using Microsoft.EntityFrameworkCore;

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

        var busyMachines = await _db.tasks
            .AsNoTracking()
            .Where(t => t.status == "Running" || t.status == "Assigned")
            .Select(t => t.machine)
            .Where(m => m != null)
            .ToListAsync();

        return machines
            .GroupBy(m => m.process_name)
            .Select(g =>
            {
                var total = g.Count();
                var busy = g.Count(m => busyMachines.Contains(m.machine_code));
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

    public Task<int> CountRunningAsync()
        => _db.tasks.AsNoTracking()
            .Where(t => t.status == "Running" && t.machine != null && t.machine != "")
            .Select(t => t.machine)
            .Distinct()
            .CountAsync();

    public async Task<List<machine>> GetActiveMachinesAsync()
    {
        return await _db.machines
            .Where(m => m.is_active == true)
            .ToListAsync();
    }

    public async Task<List<machine>> GetMachinesByProcessAsync(string processName)
    {
        return await _db.machines
            .Where(m => m.process_name == processName && m.is_active == true)
            .ToListAsync();
    }

    public async Task<Dictionary<string, decimal>> GetDailyCapacityByProcessAsync()
    {
        var result = await _db.machines
            .Where(m => m.is_active == true)
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
}

