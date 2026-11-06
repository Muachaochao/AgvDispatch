using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class AgvTransportChainLookup(ISqlSugarClient database) : IAgvTransportChainLookup
{
    public async Task<AgvTransportChain?> FindAsync(string businessTaskId,CancellationToken ct)
    {
        var rows=await database.Queryable<AgvCommandOutboxEntity>()
            .Where(c=>c.StepId==businessTaskId && c.CommandType==(int)AgvCommandType.Transport).Take(2).ToListAsync(ct);
        if(rows.Count==0)
        {
            var recovery=await database.Queryable<ObstacleRecoveryEntity>()
                .Where(row=>row.NewVendorTaskId==businessTaskId)
                .FirstAsync(ct);
            if(recovery is null)return null;
            var recoveryPath=JsonSerializer.Deserialize<string[]>(recovery.CandidatePathJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))??[];
            if(recoveryPath.Length is <2 or >2048||recoveryPath.Any(string.IsNullOrWhiteSpace)||
                string.IsNullOrWhiteSpace(recovery.VehicleId))
                throw new InvalidOperationException("障碍恢复任务链身份或候选路径无效。");
            return new(businessTaskId,recovery.VehicleId,recoveryPath.ToArray(),
                recovery.SendAttemptedAtUtc is null?0:1,recovery.SendAttemptedAtUtc is null);
        }
        if(rows.Count!=1)throw new InvalidOperationException("原运输任务链记录不唯一。");
        var row=rows[0];
        var payload=JsonSerializer.Deserialize<AgvTransportCommandPayload>(row.PayloadJson,new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("原运输负载为空。");
        if(payload.BusinessTaskId!=businessTaskId || payload.VehicleId!=row.VehicleId || payload.Path is null ||
            payload.Path.Count is < 2 or > 2048 || payload.Path.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("原运输任务链身份或路径无效。");
        return new(businessTaskId,payload.VehicleId,payload.Path.ToArray(), row.AttemptCount, row.Status == (int)AgvCommandStatus.Sending);
    }
}
