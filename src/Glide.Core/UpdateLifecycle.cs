namespace Glide.Core;

public interface IUpdateService
{
    bool Running { get; }
    void Stop();
    void Start();
}

public static class UpdateLifecycle
{
    public static void Apply(UpdateTransaction transaction, IUpdateService service)
    {
        bool wasRunning = service.Running, stopping = false;
        try
        {
            if (wasRunning) { stopping = true; service.Stop(); }
            transaction.Apply();
            if (wasRunning) service.Start();
            transaction.Commit();
        }
        catch (Exception original)
        {
            try
            {
                // A failed start can leave the new service running. It must
                // release the executable before restoring the old version.
                if (stopping && service.Running) service.Stop();
                transaction.Rollback();
                if (stopping && wasRunning) service.Start();
            }
            catch (Exception recovery)
            {
                throw new AggregateException("Update failed and recovery needs attention. Any remaining .bak files were retained beside the installation.", original, recovery);
            }
            throw;
        }
    }
}
