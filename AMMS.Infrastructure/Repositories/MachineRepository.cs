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
            if (string.IsNullOrWhiteSpace(machineCode))
                throw new InvalidOperationException("machine_code không được rỗng.");

            anchor = NormalizeSnapshotTime(anchor);

            var machine = await _db.machines
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.is_active &&
                    x.machine_code != null &&
                    x.machine_code.Trim().ToUpper() == machineCode.Trim().ToUpper(),
                    ct)
                ?? throw new InvalidOperationException($"Machine '{machineCode}' not found");

            var quantity = Math.Max(1, machine.quantity);

            var reservations = await LoadMachineReservationsForSnapshotAsync(
                machine.machine_code,
                anchor,
                ignoreOverdueOrders,
                ct);

            var lanes = BuildLaneFreeTimes(
                laneCount: quantity,
                anchor: anchor,
                reservations: reservations);

            return lanes
                .Select(NormalizeSnapshotTime)
                .OrderBy(x => x)
                .ToList();
        }

        public async Task<MachineAvailabilitySnapshotDto> GetAvailabilitySnapshotAsync(
    DateTime anchor,
    bool ignoreOverdueOrders,
    CancellationToken ct = default)
        {
            anchor = NormalizeSnapshotTime(anchor);

            var machineRows = await _db.machines
                .AsNoTracking()
                .Where(x => x.is_active)
                .OrderBy(x => x.process_code)
                .ThenBy(x => x.machine_code)
                .ToListAsync(ct);

            var result = new List<MachineAvailabilityLineDto>();

            foreach (var m in machineRows)
            {
                var quantity = Math.Max(1, m.quantity);

                var reservations = await LoadMachineReservationsForSnapshotAsync(
                    m.machine_code,
                    anchor,
                    ignoreOverdueOrders,
                    ct);

                var laneTimes = BuildLaneFreeTimes(
                    laneCount: quantity,
                    anchor: anchor,
                    reservations: reservations);

                /*
                 * FIX CHÍNH:
                 * Không tính busy_now bằng laneTimes > anchor nữa.
                 *
                 * Vì laneTimes > anchor có thể chỉ là do có lịch tương lai,
                 * nhưng hiện tại máy vẫn đang rảnh.
                 *
                 * busy_now chỉ tính các reservation đang overlap tại anchor:
                 * Start <= anchor < End.
                 */
                var busyNow = reservations
                    .Count(x => x.Start <= anchor && x.End > anchor);

                busyNow = Math.Clamp(busyNow, 0, quantity);

                var freeNow = Math.Max(0, quantity - busyNow);

                /*
                 * Nếu đang có lane rảnh thì earliest_any_lane_free_at = anchor.
                 * Nếu tất cả lane đang bận thì lấy thời điểm task đang bận kết thúc sớm nhất.
                 */
                var earliestAnyLaneFreeAt = freeNow > 0
                    ? anchor
                    : reservations
                        .Where(x => x.Start <= anchor && x.End > anchor)
                        .Select(x => x.End)
                        .DefaultIfEmpty(laneTimes.Count == 0 ? anchor : laneTimes.Min())
                        .Min();

                /*
                 * all_lanes_free_at vẫn giữ nghĩa:
                 * sau khi xét toàn bộ các reservation đã có, khi nào tất cả lane rảnh.
                 */
                var allLanesFreeAt = laneTimes.Count == 0
                    ? anchor
                    : laneTimes.Max();

                result.Add(new MachineAvailabilityLineDto
                {
                    machine_code = m.machine_code,
                    process_code = m.process_code,
                    process_name = m.process_name,

                    quantity = quantity,
                    busy_now = busyNow,
                    free_now = freeNow,

                    generated_at = anchor,
                    earliest_any_lane_free_at = earliestAnyLaneFreeAt,
                    all_lanes_free_at = allLanesFreeAt,

                    lane_free_times = laneTimes
                        .OrderBy(x => x)
                        .ToList()
                });
            }

            var workshopAllFreeAt = result.Count == 0
                ? anchor
                : result.Max(x => x.all_lanes_free_at);

            /*
             * RALO/CAT both free:
             * Lấy mốc cả RALO và CAT đều có ít nhất 1 lane free.
             */
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

            if (result.Any(x => string.Equals(x.process_code, "RALO", StringComparison.OrdinalIgnoreCase)) &&
                result.Any(x => string.Equals(x.process_code, "CAT", StringComparison.OrdinalIgnoreCase)))
            {
                raloCatBothFreeAt = raloEarliest > catEarliest
                    ? raloEarliest
                    : catEarliest;
            }

            return new MachineAvailabilitySnapshotDto
            {
                generated_at = anchor,
                workshop_all_free_at = workshopAllFreeAt,
                ralo_cat_both_free_at = raloCatBothFreeAt,
                machines = result
            };
        }

        private sealed class MachineReservationForSnapshot
        {
            public DateTime Start { get; set; }

            public DateTime End { get; set; }

            public int? TaskId { get; set; }

            public int? ProdId { get; set; }

            public string? TaskStatus { get; set; }

            public string? ProductionStatus { get; set; }
        }

        private async Task<List<MachineReservationForSnapshot>> LoadMachineReservationsForSnapshotAsync(
            string machineCode,
            DateTime anchor,
            bool ignoreOverdueOrders,
            CancellationToken ct)
        {
            anchor = NormalizeSnapshotTime(anchor);

            var machineKey = NormMachineCode(machineCode);

            if (string.IsNullOrWhiteSpace(machineKey))
                return new List<MachineReservationForSnapshot>();

            /*
             * Query đơn giản trước, filter logic phức tạp ở memory để tránh lỗi LINQ translate.
             */
            var rawRows = await (
                from t in _db.tasks.AsNoTracking()

                join p0 in _db.productions.AsNoTracking()
                    on t.prod_id equals p0.prod_id into pj
                from p in pj.DefaultIfEmpty()

                join o0 in _db.orders.AsNoTracking()
                    on p.order_id equals o0.order_id into oj
                from o in oj.DefaultIfEmpty()

                where t.machine != null &&
                      t.machine.Trim().ToUpper() == machineKey

                select new
                {
                    TaskId = t.task_id,
                    ProdId = t.prod_id,

                    TaskStatus = t.status,
                    ProductionStatus = p != null ? p.status : null,

                    Machine = t.machine,

                    PlannedStart = t.planned_start_time,
                    PlannedEnd = t.planned_end_time,

                    ActualStart = t.start_time,
                    ActualEnd = t.end_time,

                    OrderId = p != null ? p.order_id : null
                }
            ).ToListAsync(ct);

            var candidateRows = rawRows
                .Where(x => !IsNonBlockingProductionStatus(x.ProductionStatus))
                .Where(x => !IsNonBlockingTaskStatus(x.TaskStatus))
                .ToList();

            var orderIds = candidateRows
                .Where(x => x.OrderId.HasValue && x.OrderId.Value > 0)
                .Select(x => x.OrderId!.Value)
                .Distinct()
                .ToList();

            var deliveryByOrderId = new Dictionary<int, DateTime?>();

            if (orderIds.Count > 0)
            {
                var reqRows = await _db.order_requests
                    .AsNoTracking()
                    .Where(x => orderIds.Contains((int)x.order_id))
                    .Select(x => new
                    {
                        x.order_id,
                        x.order_request_id,
                        x.delivery_date
                    })
                    .ToListAsync(ct);

                deliveryByOrderId = reqRows
                    .GroupBy(x => (int)x.order_id)
                    .ToDictionary(
                        g => g.Key,
                        g => g
                            .OrderByDescending(x => x.order_request_id)
                            .Select(x => x.delivery_date)
                            .FirstOrDefault());
            }

            var reservations = new List<MachineReservationForSnapshot>();

            foreach (var row in candidateRows)
            {
                if (ignoreOverdueOrders &&
                    row.OrderId.HasValue &&
                    deliveryByOrderId.TryGetValue(row.OrderId.Value, out var deliveryDate) &&
                    deliveryDate.HasValue &&
                    deliveryDate.Value.Date < anchor.Date)
                {
                    continue;
                }

                var reservation = BuildReservationForSnapshot(
                    taskId: row.TaskId,
                    prodId: row.ProdId,
                    taskStatus: row.TaskStatus,
                    productionStatus: row.ProductionStatus,
                    plannedStart: row.PlannedStart,
                    plannedEnd: row.PlannedEnd,
                    actualStart: row.ActualStart,
                    actualEnd: row.ActualEnd,
                    anchor: anchor);

                if (reservation != null)
                    reservations.Add(reservation);
            }

            return reservations
                .Where(x => x.End > anchor || x.Start > anchor)
                .OrderBy(x => x.Start)
                .ThenBy(x => x.End)
                .ThenBy(x => x.TaskId)
                .ToList();
        }

        private static MachineReservationForSnapshot? BuildReservationForSnapshot(
            int taskId,
            int? prodId,
            string? taskStatus,
            string? productionStatus,
            DateTime? plannedStart,
            DateTime? plannedEnd,
            DateTime? actualStart,
            DateTime? actualEnd,
            DateTime anchor)
        {
            anchor = NormalizeSnapshotTime(anchor);

            /*
             * Logic đồng bộ scheduler:
             * 1. Ưu tiên actual.
             * 2. Nếu chưa có actual thì dùng planned.
             * 3. Nếu task đang giữ máy mà chưa có end_time,
             *    và planned_end đã qua, vẫn phải coi máy đang bận.
             */
            var isHoldingMachineNow = IsTaskHoldingMachineNow(
                taskStatus,
                actualStart,
                actualEnd);

            var start = actualStart ?? plannedStart;
            var end = actualEnd ?? plannedEnd;

            if (!start.HasValue && !end.HasValue)
                return null;

            if (!start.HasValue)
                start = anchor;

            if (!end.HasValue)
            {
                end = isHoldingMachineNow
                    ? anchor.AddHours(1)
                    : start.Value.AddHours(1);
            }

            start = NormalizeSnapshotTime(start.Value);
            end = NormalizeSnapshotTime(end.Value);

            if (end < start)
                end = start;

            /*
             * Nếu task đang giữ máy mà planned_end đã qua nhưng chưa finish,
             * snapshot vẫn phải thể hiện máy đang bận.
             */
            if (isHoldingMachineNow && end <= anchor)
            {
                end = anchor.AddHours(1);
            }

            /*
             * Task chưa bắt đầu, planned_end đã qua thì không block snapshot hiện tại.
             */
            if (!isHoldingMachineNow && end <= anchor)
                return null;

            /*
             * Nếu task đã actual end thì đáng lẽ status Finished.
             * Nhưng nếu DB lệch status, actualEnd <= anchor thì không block nữa.
             */
            if (actualEnd.HasValue && actualEnd.Value <= anchor)
                return null;

            return new MachineReservationForSnapshot
            {
                TaskId = taskId,
                ProdId = prodId,
                TaskStatus = taskStatus,
                ProductionStatus = productionStatus,
                Start = start.Value,
                End = end.Value
            };
        }

        private static List<DateTime> BuildLaneFreeTimes(
            int laneCount,
            DateTime anchor,
            List<MachineReservationForSnapshot> reservations)
        {
            laneCount = Math.Max(1, laneCount);
            anchor = NormalizeSnapshotTime(anchor);

            var lanes = Enumerable
                .Repeat(anchor, laneCount)
                .ToList();

            foreach (var r in reservations
                         .OrderBy(x => x.Start)
                         .ThenBy(x => x.End)
                         .ThenBy(x => x.TaskId))
            {
                AssignReservationToBestLane(
                    lanes,
                    r.Start,
                    r.End);
            }

            return lanes
                .Select(NormalizeSnapshotTime)
                .ToList();
        }

        private static void AssignReservationToBestLane(
            List<DateTime> lanes,
            DateTime start,
            DateTime end)
        {
            if (lanes == null || lanes.Count == 0)
                return;

            start = NormalizeSnapshotTime(start);
            end = NormalizeSnapshotTime(end);

            if (end <= start)
                return;

            /*
             * Nếu có lane rảnh trước thời điểm start,
             * đặt reservation vào lane rảnh sớm nhất đó.
             */
            var availableLaneIndex = -1;
            var availableLaneTime = DateTime.MaxValue;

            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] <= start && lanes[i] < availableLaneTime)
                {
                    availableLaneTime = lanes[i];
                    availableLaneIndex = i;
                }
            }

            if (availableLaneIndex >= 0)
            {
                lanes[availableLaneIndex] = end;
                return;
            }

            /*
             * Nếu tất cả lane đều đang bận ở thời điểm start,
             * gắn vào lane kết thúc sớm nhất.
             *
             * Đây là snapshot, không dời lịch thật.
             * Mục tiêu là phản ánh queue máy đang bị chồng tới lúc nào.
             */
            var earliestLaneIndex = 0;
            var earliestLaneTime = lanes[0];

            for (var i = 1; i < lanes.Count; i++)
            {
                if (lanes[i] < earliestLaneTime)
                {
                    earliestLaneTime = lanes[i];
                    earliestLaneIndex = i;
                }
            }

            lanes[earliestLaneIndex] = end > lanes[earliestLaneIndex]
                ? end
                : lanes[earliestLaneIndex];
        }

        private static bool IsTaskHoldingMachineNow(
            string? taskStatus,
            DateTime? actualStart,
            DateTime? actualEnd)
        {
            if (actualEnd.HasValue)
                return false;

            var status = NormStatus(taskStatus);

            if (status is "READY" or "INPROGRESS" or "IN_PROGRESS" or "INPROCESSING" or "PROCESSING" or "RUNNING")
                return true;

            /*
             * Nếu đã có actualStart nhưng chưa có actualEnd,
             * coi như đang giữ máy dù status bị lệch.
             */
            if (actualStart.HasValue && !actualEnd.HasValue)
                return true;

            return false;
        }

        private static bool IsNonBlockingTaskStatus(string? status)
        {
            var s = NormStatus(status);

            return s is
                "FINISHED" or
                "CANCELLED" or
                "CANCELED" or
                "SKIPPED" or
                "GROUPEDWAITING" or
                "GROUPED_WAITING";
        }

        private static bool IsNonBlockingProductionStatus(string? status)
        {
            var s = NormStatus(status);

            /*
             * Các status này xem như đã ra khỏi kế hoạch máy.
             * Scheduled/InProcessing/Pending vẫn có thể có reservation.
             */
            return s is
                "CANCELLED" or
                "CANCELED" or
                "COMPLETED" or
                "FINISHED" or
                "IMPORTING" or
                "DELIVERY";
        }

        private static string NormMachineCode(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant();
        }

        private static string NormStatus(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private static DateTime NormalizeSnapshotTime(DateTime value)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        }
    }
}
