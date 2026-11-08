namespace Skizzy.Host.Ksp
{
    internal static class BuildProbe
    {
        internal static string Describe()
        {
            return typeof(object).Assembly.ImageRuntimeVersion + " " + UnityEngine.Application.unityVersion + " " + typeof(Vessel).Assembly.GetName().Name;
        }
    }
}
