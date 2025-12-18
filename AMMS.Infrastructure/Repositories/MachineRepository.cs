using AMMS.Infrastructure.DBContext;
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
}

