using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

// A terminal business row alone cannot establish that all PLC and command
// responsibility has ended. Inspect the retained claims independently.
public sealed class ChargingBusinessSafety(ISqlSugarClient database) : IChargingBusinessSafety
{
    public async Task<string?> GetBlockReasonAsync(string vehicleId,CancellationToken ct)
    {
        var taskIds=await database.Queryable<DispatchTaskEntity>().Where(t=>t.VehicleId==vehicleId).Select(t=>t.TaskId).ToListAsync(ct);
        if(taskIds.Count>0&&await database.Queryable<TaskEquipmentClaimEntity>().AnyAsync(e=>taskIds.Contains(e.TaskId),ct))
            return "车辆原业务仍保留PLC设备归属，须完成握手释放后才能充电。";
        var sent=(int)AgvCommandStatus.Sent;var cancelled=(int)AgvCommandStatus.Cancelled;
        if(await database.Queryable<AgvCommandOutboxEntity>().AnyAsync(c=>c.VehicleId==vehicleId&&c.Status!=sent&&c.Status!=cancelled,ct))
            return "车辆存在尚未确认结束的业务命令，禁止并行充电导航。";
        return null;
    }
}
