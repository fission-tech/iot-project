using IoT.Contracts;

namespace EdgeGateway.Configuration;

// First deployment only. A pending marker makes initialization retryable if the process stops
// between creating the database and saving its container-specific broker defaults.
public static class DeploymentBootstrap
{
    public static DeviceStore Open(string databasePath, BrokerProfile? broker)
    {
        if (broker is null) return new DeviceStore(databasePath);
        broker.Validate();
        if (databasePath == ":memory:")
        {
            var memory = new DeviceStore(databasePath);
            try { Save(memory, broker); return memory; }
            catch { memory.Dispose(); throw; }
        }
        var path = Path.GetFullPath(databasePath);
        var pending = path + ".bootstrap-pending";
        var initialize = !File.Exists(path) || File.Exists(pending);
        if (initialize)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(pending, "Broker initialization pending.\n");
        }
        var store = new DeviceStore(path);
        try
        {
            if (initialize)
            {
                Save(store, broker);
                File.Delete(pending);
            }
            return store;
        }
        catch { store.Dispose(); throw; }
    }

    private static void Save(DeviceStore store, BrokerProfile broker)
    {
        if (!store.SaveBroker(broker, create: store.FindBroker(broker.Id) is null))
            throw new InvalidOperationException("Could not initialize deployment broker.");
    }
}
