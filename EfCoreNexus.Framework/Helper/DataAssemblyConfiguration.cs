using System.Reflection;

namespace EfCoreNexus.Framework.Helper;

public class DataAssemblyConfiguration
{
    public Assembly AssemblyData { get; }

    public DataAssemblyConfiguration(string assemblyName)
    {
        var assemblyData = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(x => x.GetName().Name == assemblyName);
        if (assemblyData == null)
        {
            throw new Exception("Data assembly not found");
        }

        AssemblyData = assemblyData;
    }
}