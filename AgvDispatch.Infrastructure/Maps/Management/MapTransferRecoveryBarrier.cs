using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed class MapTransferRecoveryBarrier(ISqlSugarClient database):IMapTransferRecoveryBarrier
{
    public bool IsOpen
    {
        get
        {
            try{return database.Queryable<MapTransferEntity>().Where(x=>(x.AttemptCount>0||x.State=="Sending"||x.State=="Uncertain") && x.State!="ClosedAfterReview").Count()==0;}
            catch{return false;}
        }
    }
}
