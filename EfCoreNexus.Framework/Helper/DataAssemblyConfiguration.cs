using System.Reflection;

namespace EfCoreNexus.Framework.Helper;

public class DataAssemblyConfiguration
{
    private readonly Assembly _assemblyData;

    public DataAssemblyConfiguration(string assemblyName)
    {
        var assemblyData = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(x => x.GetName().Name == assemblyName);
        if (assemblyData == null)
        {
            throw new Exception("Data assembly not found");
        }

        _assemblyData = assemblyData;
    }

    public Assembly AssemblyData => _assemblyData;
}