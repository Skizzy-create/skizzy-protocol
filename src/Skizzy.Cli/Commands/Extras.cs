using System.Collections.Generic;

namespace Skizzy.Cli
{
    public static partial class Commands
    {
        /// <summary>watch, log, shot and doctor live in their own files and register here.</summary>
        static partial void RegisterExtras(List<CommandInfo> list)
        {
            list.Add(WatchCommand.Info);
            list.Add(LogCommand.Info);
            list.Add(ShotCommand.Info);
            list.Add(DoctorCommand.Info);
        }
    }
}
