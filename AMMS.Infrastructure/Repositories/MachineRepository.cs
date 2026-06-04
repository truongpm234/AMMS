using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Estimates;
using AMMS.Shared.DTOs.Machines;
using AMMS.Shared.Helpers;
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

        public async Task<machine?> FindBestMachineByProcessCodeAsync(string processCode, CancellationToken ct = default)
        {
            var p = (processCode ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(p)) return null;

            var list = await _db.machines
                .AsNoTracking()
                .Where(m => m.is_active && m.process_code != null && m.process_code != "")
                .Where(m => m.process_code!.Trim().ToUpper() == p)
                .Select(m => new
                {
                    Machine = m,
                    Free = (m.free_quantity ?? (m.quantity - (m.busy_quantity ?? 0))),
                    Busy = (m.busy_quantity ?? 0),
                    Cap = m.capacity_per_hour
                })
                .ToListAsync(ct);

            if (list.Count == 0) return null;

            var anyFree = list.Any(x => x.Free > 0);

            var best = anyFree
                ? list.OrderByDescending(x => x.Free)
                      .ThenBy(x => x.Busy)
                      .ThenByDescending(x => x.Cap)
                      .ThenBy(x => x.Machine.machine_id)
                      .FirstOrDefault()
                : list.OrderBy(x => x.Busy)             
                      .ThenByDescending(x => x.Cap)
                      .ThenBy(x => x.Machine.machine_id)
                      .FirstOrDefault();

            return best?.Machine;
        }

        public async Task<List<DateTime>> GetLaneAvailableTimesAsync(
    string machineCode,
    DateTime anchor,
    bool ignoreOverdueOrders,
    CancellationToken ct = default)
        {
            var machine = await _db.machines
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.machine_code == machineCode && x.is_active, ct)
                ?? throw new InvalidOperationException($"Machine '{machineCode}' not found");

            var laneCount = Math.Max(1, machine.quantity);
            var lanes = Enumerable.Repeat(anchor, laneCount).ToList();

            var machineKey = machineCode.Trim().ToUpperInvariant();

            var rows = await (
                from t in _db.tasks.AsNoTracking()

                join p0 in _db.productions.AsNoTracking()
                    on t.prod_id equals p0.prod_id into pj
                from p in pj.DefaultIfEmpty()

                join o0 in _db.orders.AsNoTracking()
                    on p.order_id equals o0.order_id into oj
                from o in oj.DefaultIfEmpty()

                join r0 in _db.order_requests.AsNoTracking()
                    on o.order_id equals r0.order_id into rj
                from r in rj.OrderByDescending(x => x.order_request_id).Take(1).DefaultIfEmpty()

                where t.machine != null
                      && t.machine.Trim().ToUpper() == machineKey
                      && !(
                            t.status != null &&
                            t.status.Trim().ToUpper() == "FINISHED"
                         )
                      && (
                            p == null ||
                            p.status == null ||
                            (
                                p.status.ToUpper() != "CANCELLED" &&
                                p.status.ToUpper() != "COMPLETED" &&
                                p.status.ToUpper() != "FINISHED" &&
                                p.status.ToUpper() != "IMPORTING" &&
                                p.status.ToUpper() != "DELIVERY"
                            )
                         )

                select new
                {
                    /*
                     * FIX:
                     * Ưu tiên actual trước.
                     * Nếu order cũ xong sớm, slot máy được giải phóng theo end_time thật.
                     */
                    Start = t.start_time ?? t.planned_start_time ?? anchor,

                    End = t.end_time
                          ?? t.planned_end_time
                          ?? ((t.start_time ?? t.planned_start_time ?? anchor).AddHours(1)),

                    DeliveryDate = r != null ? r.delivery_date : null
                }
            ).ToListAsync(ct);

            var reservations = rows
                .Where(x => !ignoreOverdueOrders || !x.DeliveryDate.HasValue || x.DeliveryDate.Value >= anchor)
                .Select(x => new
                {
                    Start = DateTime.SpecifyKind(x.Start, DateTimeKind.Unspecified),
                    End = DateTime.SpecifyKind(x.End < x.Start ? x.Start : x.End, DateTimeKind.Unspecified)
                })
                .Where(x => x.End > anchor)
                .OrderBy(x => x.Start)
                .ToList();

            foreach (var r in reservations)
            {
                MachineHelper.AssignReservationToLane(
                    lanes,
                    r.Start,
                    r.End);
            }

            return lanes;
        }

        public async Task<MachineAvailabilitySnapshotDto> GetAvailabilitySnapshotAsync(
    DateTime anchor,
    bool ignoreOverdueOrders,
    CancellationToken ct = default)
        {
            var machineRows = await _db.machines
                .AsNoTracking()
                .Where(x => x.is_active)
                .OrderBy(x => x.process_code)
                .ThenBy(x => x.machine_code)
                .ToListAsync(ct);

            var result = new List<MachineAvailabilityLineDto>();

            foreach (var m in machineRows)
            {
                var laneTimes = await GetLaneAvailableTimesAsync(m.machine_code, anchor, ignoreOverdueOrders, ct);
                var busyNow = laneTimes.Count(x => x > anchor);
                var freeNow = Math.Max(0, Math.Max(1, m.quantity) - busyNow);
                var earliest = laneTimes.Count == 0 ? anchor : laneTimes.Min();
                var allFree = laneTimes.Count == 0 ? anchor : laneTimes.Max();

                result.Add(new MachineAvailabilityLineDto
                {
                    machine_code = m.machine_code,
                    process_code = m.process_code,
                    process_name = m.process_name,
                    quantity = Math.Max(1, m.quantity),
                    busy_now = busyNow,
                    free_now = freeNow,
                    generated_at = anchor,
                    earliest_any_lane_free_at = earliest,
                    all_lanes_free_at = allFree,
                    lane_free_times = laneTimes.OrderBy(x => x).ToList()
                });
            }

            var workshopAllFreeAt = result.Count == 0 ? anchor : result.Max(x => x.all_lanes_free_at);

            var raloEarliest = result
                .Where(x => string.Equals(x.process_code, "RALO", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.earliest_any_lane_free_at)
                .DefaultIfEmpty(anchor)
                .Max();

            var catEarliest = result
                .Where(x => string.Equals(x.process_code, "CAT", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.earliest_any_lane_free_at)
                .DefaultIfEmpty(anchor)
                .Max();

            DateTime? raloCatBothFreeAt = null;
            if (result.Any(x => string.Equals(x.process_code, "RALO", StringComparison.OrdinalIgnoreCase))
                && result.Any(x => string.Equals(x.process_code, "CAT", StringComparison.OrdinalIgnoreCase)))
            {
                raloCatBothFreeAt = raloEarliest > catEarliest ? raloEarliest : catEarliest;
            }

            return new MachineAvailabilitySnapshotDto
            {
                generated_at = anchor,
                workshop_all_free_at = workshopAllFreeAt,
                ralo_cat_both_free_at = raloCatBothFreeAt,
                machines = result
            };
        }

    }
}
